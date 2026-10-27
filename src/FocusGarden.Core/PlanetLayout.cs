using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;

namespace FocusGarden.Core;

/// <summary>A stable object placed on one of the planet's buildable surface plots.</summary>
public sealed record PlanetBuildingPlacement(string Id, string Kind, int PlotId, int QuarterTurns);

public sealed record PlanetBuildingDefinition(string Kind, string Name, int RequiredLevel, int MaxCount);

/// <summary>Growth unlocks reusable scenery; placing or moving it never spends growth.</summary>
public static class PlanetLayoutCatalog
{
    public const int PlotCount = 32;

    public static IReadOnlyList<PlanetBuildingDefinition> Entries { get; } =
        new ReadOnlyCollection<PlanetBuildingDefinition>(
        [
            new("tree", "树木", 1, 12),
            new("flower", "花丛", 1, 12),
            new("house", "小屋", 3, 8),
            new("windmill", "风车", 4, 3),
            new("tower", "观测塔", 4, 2),
            new("observatory", "天文台", 5, 1)
        ]);

    public static PlanetBuildingDefinition Get(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var normalized = kind.Trim().ToLowerInvariant();
        return Entries.FirstOrDefault(x => x.Kind == normalized)
            ?? throw new ArgumentException("未知的景观或建筑类型。", nameof(kind));
    }
}

/// <summary>
/// Persists one layout per stable planet ID. All edits commit atomically and a plot
/// can hold at most one object. Existing buildings stay put if a caller later has
/// a lower level; level gates apply only to placing additional objects.
/// </summary>
public sealed class PlanetLayoutStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private bool disposed;

    public PlanetLayoutStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath != ":memory:")
        {
            dbPath = Path.GetFullPath(dbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=3000;
            CREATE TABLE IF NOT EXISTS planet_buildings (
                planet_id TEXT NOT NULL,
                id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('tree','flower','house','windmill','tower','observatory')),
                plot_id INTEGER NOT NULL CHECK(plot_id >= 0 AND plot_id < 32),
                quarter_turns INTEGER NOT NULL CHECK(quarter_turns >= 0 AND quarter_turns < 4),
                PRIMARY KEY(planet_id,id),
                UNIQUE(planet_id,plot_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PlanetBuildingPlacement> GetBuildings(string planetId)
    {
        planetId = ValidateIdentifier(planetId, nameof(planetId));
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT id,kind,plot_id,quarter_turns FROM planet_buildings WHERE planet_id=$planet ORDER BY plot_id", null, ("$planet", planetId));
            using var reader = command.ExecuteReader();
            var buildings = new List<PlanetBuildingPlacement>();
            while (reader.Read()) buildings.Add(ReadPlacement(reader));
            return buildings.AsReadOnly();
        }
    }

    public PlanetBuildingPlacement Place(string planetId, string kind, int plotId, int level)
    {
        planetId = ValidateIdentifier(planetId, nameof(planetId));
        var definition = PlanetLayoutCatalog.Get(kind);
        ValidatePlot(plotId);
        if (level is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(level), "星球等级必须在 0–5 之间。");
        if (level < definition.RequiredLevel)
            throw new InvalidOperationException($"{definition.Name}将在 {definition.RequiredLevel} 级解锁。");
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsurePlotAvailable(planetId, plotId, null, transaction);
            using var countCommand = Command("SELECT COUNT(*) FROM planet_buildings WHERE planet_id=$planet AND kind=$kind", transaction,
                ("$planet", planetId), ("$kind", definition.Kind));
            if (Convert.ToInt64(countCommand.ExecuteScalar()) >= definition.MaxCount)
                throw new InvalidOperationException($"每颗星球最多可放置 {definition.MaxCount} 个{definition.Name}。可移动或收起已有物品。");
            var placement = new PlanetBuildingPlacement(Guid.NewGuid().ToString("N"), definition.Kind, plotId, 0);
            using var insert = Command("INSERT INTO planet_buildings(planet_id,id,kind,plot_id,quarter_turns) VALUES($planet,$id,$kind,$plot,0)", transaction,
                ("$planet", planetId), ("$id", placement.Id), ("$kind", placement.Kind), ("$plot", plotId));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return placement;
        }
    }

    public PlanetBuildingPlacement Move(string planetId, string id, int plotId)
    {
        planetId = ValidateIdentifier(planetId, nameof(planetId));
        id = ValidateIdentifier(id, nameof(id));
        ValidatePlot(plotId);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction(deferred: false);
            var existing = FindBuilding(planetId, id, transaction);
            EnsurePlotAvailable(planetId, plotId, id, transaction);
            using var command = Command("UPDATE planet_buildings SET plot_id=$plot WHERE planet_id=$planet AND id=$id", transaction,
                ("$plot", plotId), ("$planet", planetId), ("$id", id));
            command.ExecuteNonQuery();
            transaction.Commit();
            return existing with { PlotId = plotId };
        }
    }

    public PlanetBuildingPlacement Rotate(string planetId, string id)
    {
        planetId = ValidateIdentifier(planetId, nameof(planetId));
        id = ValidateIdentifier(id, nameof(id));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction(deferred: false);
            var existing = FindBuilding(planetId, id, transaction);
            var rotation = (existing.QuarterTurns + 1) % 4;
            using var command = Command("UPDATE planet_buildings SET quarter_turns=$rotation WHERE planet_id=$planet AND id=$id", transaction,
                ("$rotation", rotation), ("$planet", planetId), ("$id", id));
            command.ExecuteNonQuery();
            transaction.Commit();
            return existing with { QuarterTurns = rotation };
        }
    }

    public bool Remove(string planetId, string id)
    {
        planetId = ValidateIdentifier(planetId, nameof(planetId));
        id = ValidateIdentifier(id, nameof(id));
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("DELETE FROM planet_buildings WHERE planet_id=$planet AND id=$id", null,
                ("$planet", planetId), ("$id", id));
            return command.ExecuteNonQuery() > 0;
        }
    }

    private PlanetBuildingPlacement FindBuilding(string planetId, string id, SqliteTransaction transaction)
    {
        using var command = Command("SELECT id,kind,plot_id,quarter_turns FROM planet_buildings WHERE planet_id=$planet AND id=$id", transaction,
            ("$planet", planetId), ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("当前星球上没有找到这件物品，请重新选择。");
        return ReadPlacement(reader);
    }

    private void EnsurePlotAvailable(string planetId, int plotId, string? excludedId, SqliteTransaction transaction)
    {
        using var command = Command("SELECT id FROM planet_buildings WHERE planet_id=$planet AND plot_id=$plot", transaction,
            ("$planet", planetId), ("$plot", plotId));
        var occupant = command.ExecuteScalar() as string;
        if (occupant != null && occupant != excludedId)
            throw new InvalidOperationException("这块地已经有物品了，请选择空地，或先移动原来的物品。");
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static PlanetBuildingPlacement ReadPlacement(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));

    private static string ValidateIdentifier(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > 128 || value.Any(char.IsControl) || value != value.Trim())
            throw new ArgumentException("星球或物品标识无效。", name);
        return value;
    }

    private static void ValidatePlot(int plotId)
    {
        if (plotId is < 0 or >= PlanetLayoutCatalog.PlotCount)
            throw new ArgumentOutOfRangeException(nameof(plotId), $"请选择 1–{PlanetLayoutCatalog.PlotCount} 号地块。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            connection.Dispose();
        }
    }
}
