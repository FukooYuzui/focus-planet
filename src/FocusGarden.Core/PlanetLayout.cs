using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FocusGarden.Core;

/// <summary>A stable object placed on one of the planet's buildable surface plots.</summary>
public sealed record PlanetBuildingPlacement(string Id, string Kind, int PlotId, int QuarterTurns);

public sealed record PlanetBuildingDefinition(string Kind, string Name, int RequiredLevel, int MaxCount,
    string Category = "建筑", string Hint = "");

/// <summary>Growth unlocks reusable scenery; placing or moving it never spends growth.</summary>
public static class PlanetLayoutCatalog
{
    public const int PlotCount = 32;
    public const int AnimalLimitPerPlanet = 12;

    public static IReadOnlyList<PlanetBuildingDefinition> Entries { get; } =
        new ReadOnlyCollection<PlanetBuildingDefinition>(
        [
            new("tree", "树木", 1, 12, "景观", "让树荫陪伴专注，可移动或旋转来调整位置。"),
            new("flower", "花丛", 1, 12, "景观", "用花丛点缀地表，可与其他景观组合。"),
            new("house", "小屋", 3, 8, Hint: "为小星球安置一个家，可自由调整朝向。"),
            new("windmill", "风车", 4, 3, Hint: "把风车放在开阔处，为家园增添地标。"),
            new("tower", "观测塔", 4, 2, Hint: "在地表留下一座观察远方的塔。"),
            new("observatory", "天文台", 5, 1, Hint: "为星空探索留一个位置。"),
            new("rabbit", "兔子", 2, 3, "生物", "观察时会自动靠近附近的草地、花丛或灌木。"),
            new("sheep", "绵羊", 2, 3, "生物", "观察时会自动在附近的草地、果树或树木旁活动。"),
            new("fox", "狐狸", 2, 2, "生物", "观察时会自动靠近附近的灌木、树木或蘑菇丛。"),
            new("duck", "鸭子", 2, 3, "生物", "附近有池塘时，会自动前往水边活动。"),
            new("deer", "鹿", 2, 2, "生物", "观察时会自动靠近附近的池塘、树木或果树。"),
            new("butterfly", "蝴蝶", 2, 3, "生物", "观察时会自动飞向附近的花丛、果树或蘑菇丛。"),
            new("pond", "池塘", 1, 3, "景观", "在地表留一片水面，可搭配鸭子与灌木。"),
            new("meadow", "草地", 1, 6, "景观", "铺开一小片草地，给家园添一点绿。"),
            new("mushroom", "蘑菇丛", 1, 6, "景观", "用蘑菇丛点缀树木和草地之间。"),
            new("orchard", "果树", 1, 4, "景观", "把果树安置在喜欢的位置，可调整朝向。"),
            new("rockgarden", "岩石庭园", 0, 4, "景观", "初生星球也能布置岩石庭园。"),
            new("bush", "灌木", 1, 6, "景观", "用灌木围出小径与生物栖息的角落。")
        ]);

    public static PlanetBuildingDefinition Get(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var normalized = kind.Trim().ToLowerInvariant();
        return Entries.FirstOrDefault(x => x.Kind == normalized)
            ?? throw new ArgumentException("未知的生物、景观或建筑类型。", nameof(kind));
    }

    public static bool IsAnimal(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        var normalized = kind.Trim().ToLowerInvariant();
        return Entries.Any(x => x.Kind == normalized && x.Category == "生物");
    }
}

/// <summary>
/// Persists one layout per stable planet ID. All edits commit atomically and a plot
/// can hold at most one object. Existing buildings stay put if a caller later has
/// a lower level; level gates apply only to placing additional objects.
/// </summary>
public sealed class PlanetLayoutStore : IDisposable
{
    public const int SchemaVersion = 1;
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private bool disposed;

    public PlanetLayoutStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath != ":memory:")
        {
            dbPath = Path.GetFullPath(dbPath);
            if (string.Equals(Path.GetFileName(dbPath), "focus.db", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("布局存储不能打开专注记录数据库。", nameof(dbPath));
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 3
        }.ToString());
        try
        {
            connection.Open();
            InitializeSchema(dbPath);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000;";
            command.ExecuteNonQuery();
        }
        catch { connection.Dispose(); throw; }
    }

    private static string CreateTableSql(string tableName) => $"""
        CREATE TABLE {tableName} (
            planet_id TEXT NOT NULL,
            id TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ({string.Join(",", PlanetLayoutCatalog.Entries.Select(x => "'" + x.Kind.Replace("'", "''") + "'"))})),
            plot_id INTEGER NOT NULL CHECK(plot_id >= 0 AND plot_id < {PlanetLayoutCatalog.PlotCount}),
            quarter_turns INTEGER NOT NULL CHECK(quarter_turns >= 0 AND quarter_turns < 4),
            PRIMARY KEY(planet_id,id),
            UNIQUE(planet_id,plot_id)
        );
        """;

    private void InitializeSchema(string dbPath)
    {
        using var versionCommand = Command("PRAGMA user_version", null);
        var version = Convert.ToInt64(versionCommand.ExecuteScalar());
        if (version is < 0 or > SchemaVersion)
            throw new InvalidOperationException("布局数据库来自不兼容的较新版本；已有家园保持不变，请使用对应版本打开。");
        using var exists = Command("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='planet_buildings'", null);
        var hasLayout = Convert.ToInt64(exists.ExecuteScalar()) != 0;
        if (!hasLayout)
        {
            using var others = Command("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", null);
            if (version != 0 || Convert.ToInt64(others.ExecuteScalar()) != 0)
                throw new InvalidOperationException("这份数据库缺少有效布局，未创建空布局或覆盖原文件。");
            using var create = connection.BeginTransaction(deferred: false);
            using var command = Command(CreateTableSql("planet_buildings") + $" PRAGMA user_version={SchemaVersion};", create);
            command.ExecuteNonQuery(); create.Commit();
            return;
        }
        ValidateColumns(connection);
        if (version == SchemaVersion) return;

        // The backup is committed and independently readable before any schema mutation begins.
        var before = dbPath == ":memory:" ? LayoutFingerprint(connection) : BackupLegacyLayout(dbPath);
        using var migration = connection.BeginTransaction(deferred: false);
        if (before != LayoutFingerprint(connection, migration))
            throw new IOException("备份后布局被其他窗口修改，本次未迁移；请关闭其他窗口后重试。");
        using (var command = Command(CreateTableSql("planet_buildings_v1_migration"), migration)) command.ExecuteNonQuery();
        using (var command = Command("""
            INSERT INTO planet_buildings_v1_migration(planet_id,id,kind,plot_id,quarter_turns)
            SELECT planet_id,id,kind,plot_id,quarter_turns FROM planet_buildings;
            """, migration)) command.ExecuteNonQuery();
        if (before != LayoutFingerprint(connection, migration, "planet_buildings_v1_migration"))
            throw new IOException("迁移前后布局记录不一致，已取消本次迁移。");
        using (var command = Command($"""
            DROP TABLE planet_buildings;
            ALTER TABLE planet_buildings_v1_migration RENAME TO planet_buildings;
            PRAGMA user_version={SchemaVersion};
            """, migration)) command.ExecuteNonQuery();
        RequireHealthy(connection, migration);
        migration.Commit();
    }

    private string BackupLegacyLayout(string dbPath)
    {
        var backups = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
        Directory.CreateDirectory(backups);
        var final = Path.Combine(backups, $"planet-layout-before-v{SchemaVersion}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.db");
        var temporary = final + ".partial";
        string fingerprint;
        using (var snapshot = connection.BeginTransaction(deferred: true))
        {
            RequireHealthy(connection, snapshot);
            fingerprint = LayoutFingerprint(connection, snapshot);
            using (File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                backup.Open(); connection.BackupDatabase(backup);
                RequireHealthy(backup);
                if (fingerprint != LayoutFingerprint(backup)) throw new IOException("布局备份内容校验失败，原布局未修改。");
                using var finish = backup.CreateCommand(); finish.CommandText = "PRAGMA journal_mode=DELETE";
                if (!string.Equals(finish.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("无法生成独立布局备份，原布局未修改。");
            }
            snapshot.Commit();
        }
        using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            verify.Open(); RequireHealthy(verify);
            if (fingerprint != LayoutFingerprint(verify)) throw new IOException("布局备份重新打开后校验失败，原布局未修改。");
            using var mode = verify.CreateCommand(); mode.CommandText = "PRAGMA journal_mode";
            if (!string.Equals(mode.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                throw new IOException("布局备份仍依赖日志文件，本次未迁移。");
        }
        File.Move(temporary, final);
        return fingerprint;
    }

    private static void ValidateColumns(SqliteConnection database)
    {
        using var command = database.CreateCommand(); command.CommandText = "PRAGMA table_info(planet_buildings)";
        using var reader = command.ExecuteReader(); var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        if (!names.SequenceEqual(new[] { "planet_id", "id", "kind", "plot_id", "quarter_turns" }))
            throw new InvalidOperationException("布局字段不兼容，未改写原布局。");
    }

    private static string LayoutFingerprint(SqliteConnection database, SqliteTransaction? transaction = null, string tableName = "planet_buildings")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT planet_id,id,kind,plot_id,quarter_turns FROM {tableName} ORDER BY planet_id,id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount]; reader.GetValues(row);
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void RequireHealthy(SqliteConnection database, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA quick_check";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new IOException("布局数据库完整性检查未通过，原文件已保留。");
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
            if (definition.Category == "生物")
            {
                var kinds = string.Join(",", PlanetLayoutCatalog.Entries.Where(x => x.Category == "生物").Select(x => "'" + x.Kind + "'"));
                using var animalCount = Command($"SELECT COUNT(*) FROM planet_buildings WHERE planet_id=$planet AND kind IN ({kinds})", transaction, ("$planet", planetId));
                if (Convert.ToInt64(animalCount.ExecuteScalar()) >= PlanetLayoutCatalog.AnimalLimitPerPlanet)
                    throw new InvalidOperationException($"每颗星球最多可放置 {PlanetLayoutCatalog.AnimalLimitPerPlanet} 只生物，让小世界保持轻快。可先收起已有生物。");
            }
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
