using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FocusGarden.Core;

/// <summary>
/// Stores growth independently from source records. Initialize once before use. Call GetTokenPeriodStart
/// before importing Token events, then pass only events at or after its returned timestamp to Update.
/// Replayed and decreased daily observations never remove growth or award the same usage twice.
/// </summary>
public sealed class PlanetGrowthStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public PlanetGrowthStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (dbPath != ":memory:")
        {
            dbPath = Path.GetFullPath(dbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        }
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            // Growth refresh is secondary UI work. If another writer holds this
            // database, retry on the next refresh instead of blocking focus for 30 seconds.
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 1
        }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=1000;
            CREATE TABLE IF NOT EXISTS planet_state (
                id INTEGER PRIMARY KEY CHECK(id=1),
                activated_at TEXT NOT NULL,
                activation_day TEXT NOT NULL,
                focus_baseline INTEGER NOT NULL CHECK(focus_baseline>=0),
                current_planet_id INTEGER NOT NULL,
                token_source TEXT,
                token_period_start TEXT
            );
            CREATE TABLE IF NOT EXISTS planets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                activated_at TEXT NOT NULL,
                seed INTEGER NOT NULL,
                parent_id INTEGER UNIQUE REFERENCES planets(id)
            );
            CREATE TABLE IF NOT EXISTS planet_days (
                day TEXT PRIMARY KEY,
                focus_seconds INTEGER NOT NULL DEFAULT 0 CHECK(focus_seconds>=0),
                tokens INTEGER NOT NULL DEFAULT 0 CHECK(tokens>=0),
                awarded INTEGER NOT NULL DEFAULT 0 CHECK(awarded>=0 AND awarded<=200)
            );
            CREATE TABLE IF NOT EXISTS planet_token_observations (
                day TEXT PRIMARY KEY,
                high_water INTEGER NOT NULL CHECK(high_water>=0)
            );
            CREATE TABLE IF NOT EXISTS planet_growth_credits (
                planet_id INTEGER NOT NULL REFERENCES planets(id),
                day TEXT NOT NULL,
                growth INTEGER NOT NULL CHECK(growth>=0 AND growth<=200),
                PRIMARY KEY(planet_id,day)
            );
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    public void Initialize(DateTimeOffset now, IReadOnlyDictionary<string, int> focusDailyTotals)
    {
        ArgumentNullException.ThrowIfNull(focusDailyTotals);
        var day = LocalDay(now);
        var baseline = focusDailyTotals.TryGetValue(day, out var seconds) ? Math.Max(0, seconds) : 0;
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction();
            using var existing = Command(transaction, "SELECT COUNT(*) FROM planet_state WHERE id=1");
            if ((long)existing.ExecuteScalar()! > 0) { transaction.Commit(); return; }
            var planetId = CreatePlanet(now, null, transaction);
            using var command = Command(transaction, """
                INSERT INTO planet_state(id,activated_at,activation_day,focus_baseline,current_planet_id)
                VALUES(1,$activated,$day,$baseline,$planet)
                """);
            command.Parameters.AddWithValue("$activated", Format(now));
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$baseline", baseline);
            command.Parameters.AddWithValue("$planet", planetId);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public PlanetSnapshot Update(DateTimeOffset now, IReadOnlyDictionary<string, int> focusDailyTotals,
        IReadOnlyDictionary<string, long> tokenDailySincePeriod)
    {
        ArgumentNullException.ThrowIfNull(focusDailyTotals);
        ArgumentNullException.ThrowIfNull(tokenDailySincePeriod);
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction();
            var state = ReadState(transaction);
            var today = LocalDay(now);
            var touched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (day, observed) in focusDailyTotals)
            {
                if (!EligibleDay(day, state.ActivationDay, today) || observed <= 0) continue;
                var seconds = day == state.ActivationDay ? Math.Max(0, observed - state.FocusBaseline) : observed;
                using var command = Command(transaction, """
                    INSERT INTO planet_days(day,focus_seconds) VALUES($day,$seconds)
                    ON CONFLICT(day) DO UPDATE SET focus_seconds=excluded.focus_seconds
                    WHERE excluded.focus_seconds > planet_days.focus_seconds
                    """);
                command.Parameters.AddWithValue("$day", day);
                command.Parameters.AddWithValue("$seconds", seconds);
                if (command.ExecuteNonQuery() > 0) touched.Add(day);
            }

            var tokenFirstDay = state.TokenPeriodStart is { } start ? LocalDay(start) : state.ActivationDay;
            foreach (var (day, observed) in tokenDailySincePeriod)
            {
                if (!EligibleDay(day, tokenFirstDay, today) || observed <= 0) continue;
                if (state.TokenPeriodStart is null)
                    throw new InvalidOperationException("请先绑定 Token 数据来源，再更新星球成长。");

                using var query = Command(transaction, "SELECT high_water FROM planet_token_observations WHERE day=$day");
                query.Parameters.AddWithValue("$day", day);
                var prior = query.ExecuteScalar() is long value ? value : 0;
                if (observed <= prior) continue;
                var delta = observed - prior;
                using var update = Command(transaction, """
                    INSERT INTO planet_token_observations(day,high_water) VALUES($day,$observed)
                    ON CONFLICT(day) DO UPDATE SET high_water=excluded.high_water;
                    INSERT INTO planet_days(day,tokens) VALUES($day,$delta)
                    ON CONFLICT(day) DO UPDATE SET tokens=CASE
                        WHEN tokens > $maximum - $delta THEN $maximum ELSE tokens + $delta END;
                    """);
                update.Parameters.AddWithValue("$day", day);
                update.Parameters.AddWithValue("$observed", observed);
                update.Parameters.AddWithValue("$delta", delta);
                update.Parameters.AddWithValue("$maximum", long.MaxValue);
                update.ExecuteNonQuery();
                touched.Add(day);
            }

            long newGrowth = 0;
            foreach (var day in touched)
            {
                var row = ReadDay(day, transaction);
                var target = PlanetGrowthRules.Calculate(row.FocusSeconds, row.Tokens).TotalGrowth;
                if (target <= row.Awarded) continue;
                using var award = Command(transaction, "UPDATE planet_days SET awarded=$growth WHERE day=$day");
                award.Parameters.AddWithValue("$day", day);
                award.Parameters.AddWithValue("$growth", target);
                award.ExecuteNonQuery();
                var delta = target - row.Awarded;
                using var credit = Command(transaction, """
                    INSERT INTO planet_growth_credits(planet_id,day,growth) VALUES($planet,$day,$delta)
                    ON CONFLICT(planet_id,day) DO UPDATE SET growth=growth+excluded.growth
                    """);
                credit.Parameters.AddWithValue("$planet", state.CurrentPlanetId);
                credit.Parameters.AddWithValue("$day", day);
                credit.Parameters.AddWithValue("$delta", delta);
                credit.ExecuteNonQuery();
                newGrowth += delta;
            }
            var snapshot = ReadSnapshot(now, state, transaction, newGrowth);
            transaction.Commit();
            return snapshot;
        }
    }

    public PlanetSnapshot GetSnapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var result = ReadSnapshot(now, ReadState(transaction), transaction, 0);
            transaction.Commit();
            return result;
        }
    }

    public void SetName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (new StringInfo(name).LengthInTextElements > 20 || name.Any(char.IsControl))
            throw new ArgumentException("星球名称应为 1–20 个字符，且不能包含控制字符。", nameof(name));
        lock (_gate)
        {
            CheckDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE planets SET name=$name WHERE id=(SELECT current_planet_id FROM planet_state WHERE id=1)";
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteNonQuery() != 1) throw NotInitialized();
        }
    }

    public IReadOnlyList<PlanetSummary> ListPlanets()
    {
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var state = ReadState(transaction);
            using var query = Command(transaction, """
                SELECT p.id,p.name,p.activated_at,COALESCE(SUM(c.growth),0)
                FROM planets p LEFT JOIN planet_growth_credits c ON c.planet_id=p.id
                GROUP BY p.id,p.name,p.activated_at ORDER BY p.id
                """);
            var result = new List<PlanetSummary>();
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                    result.Add(new PlanetSummary(reader.GetInt64(0), reader.GetString(1), Parse(reader.GetString(2)),
                        reader.GetInt64(3), reader.GetInt64(0) == state.CurrentPlanetId));
            }
            transaction.Commit();
            return result.AsReadOnly();
        }
    }

    public void SelectPlanet(long id)
    {
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction();
            _ = ReadState(transaction);
            using var command = Command(transaction, """
                UPDATE planet_state SET current_planet_id=$id
                WHERE id=1 AND EXISTS(SELECT 1 FROM planets WHERE id=$id)
                """);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() != 1) throw new ArgumentException("小星球不存在。", nameof(id));
            transaction.Commit();
        }
    }

    public PlanetSnapshot ExploreNewPlanet(DateTimeOffset now)
    {
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction();
            var state = ReadState(transaction);
            var current = ReadSnapshot(now, state, transaction, 0);
            if (!current.CanExplore)
                throw new InvalidOperationException("星球满 6 个自然月后可以开拓一次新星球。");
            var id = CreatePlanet(now, state.CurrentPlanetId, transaction);
            using var update = Command(transaction, "UPDATE planet_state SET current_planet_id=$id WHERE id=1");
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            var result = ReadSnapshot(now, state with { CurrentPlanetId = id }, transaction, 0);
            transaction.Commit();
            return result;
        }
    }

    public DateTimeOffset GetTokenPeriodStart(string sourceRoot, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot.Trim()));
        lock (_gate)
        {
            CheckDisposed();
            using var transaction = _connection.BeginTransaction();
            var state = ReadState(transaction);
            if (string.Equals(sourceRoot, state.TokenSource, StringComparison.OrdinalIgnoreCase))
            {
                transaction.Commit();
                return state.TokenPeriodStart!.Value;
            }
            var cutoff = state.TokenSource is null ? state.ActivatedAt : now;
            // Clock rollback must never reopen previously credited history on a source switch.
            if (cutoff < state.ActivatedAt) cutoff = state.ActivatedAt;
            if (state.TokenPeriodStart is { } previous && cutoff < previous) cutoff = previous;
            using var update = Command(transaction, """
                UPDATE planet_state SET token_source=$source,token_period_start=$start WHERE id=1;
                DELETE FROM planet_token_observations;
                """);
            update.Parameters.AddWithValue("$source", sourceRoot);
            update.Parameters.AddWithValue("$start", Format(cutoff));
            update.ExecuteNonQuery();
            transaction.Commit();
            return cutoff;
        }
    }

    private PlanetSnapshot ReadSnapshot(DateTimeOffset now, State state, SqliteTransaction transaction, long newGrowth)
    {
        var planet = ReadPlanet(state.CurrentPlanetId, transaction);
        using var query = Command(transaction, "SELECT COALESCE(SUM(growth),0) FROM planet_growth_credits WHERE planet_id=$id");
        query.Parameters.AddWithValue("$id", planet.Id);
        var total = (long)query.ExecuteScalar()!;
        var day = LocalDay(now);
        var today = ReadDay(day, transaction);
        var points = PlanetGrowthRules.Calculate(today.FocusSeconds, today.Tokens);
        var level = PlanetGrowthRules.GetLevel(total, planet.ActivatedAt, now);
        var (weather, night) = PlanetGrowthRules.GetWeather(planet.Seed, now, level);
        using var dailyCredit = Command(transaction, "SELECT growth FROM planet_growth_credits WHERE planet_id=$id AND day=$day");
        dailyCredit.Parameters.AddWithValue("$id", planet.Id);
        dailyCredit.Parameters.AddWithValue("$day", day);
        var todayPlanetGrowth = dailyCredit.ExecuteScalar() is long growth ? (int)growth : 0;
        var cycleEndsAt = planet.ActivatedAt.AddMonths(6);
        return new PlanetSnapshot(planet.Id, planet.Name, planet.ActivatedAt, cycleEndsAt,
            now >= cycleEndsAt && !planet.HasChild,
            level == PlanetGrowthRules.LevelThresholds.Count - 1 ? null : planet.ActivatedAt.AddMonths(PlanetGrowthRules.MinimumAgeMonths[level + 1]),
            Math.Max(0, (int)(now - planet.ActivatedAt).TotalDays), total, level, PlanetGrowthRules.GetLevelName(level),
            level == PlanetGrowthRules.LevelThresholds.Count - 1 ? null : PlanetGrowthRules.LevelThresholds[level + 1],
            points.FocusPoints, points.TokenPoints, points.SynergyPoints, today.Awarded, todayPlanetGrowth, today.FocusSeconds,
            today.Tokens, newGrowth, level > 0 ? PlanetGrowthRules.GetUnlock(level) : null, weather, night, planet.Seed);
    }

    private State ReadState(SqliteTransaction transaction)
    {
        using var query = Command(transaction, """
            SELECT activated_at,activation_day,focus_baseline,current_planet_id,token_source,token_period_start
            FROM planet_state WHERE id=1
            """);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw NotInitialized();
        return new State(Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : Parse(reader.GetString(5)));
    }

    private Planet ReadPlanet(long id, SqliteTransaction transaction)
    {
        using var query = Command(transaction, """
            SELECT id,name,activated_at,seed,EXISTS(SELECT 1 FROM planets child WHERE child.parent_id=p.id)
            FROM planets p WHERE id=$id
            """);
        query.Parameters.AddWithValue("$id", id);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("当前小星球不存在。");
        return new Planet(reader.GetInt64(0), reader.GetString(1), Parse(reader.GetString(2)), reader.GetInt32(3), reader.GetBoolean(4));
    }

    private long CreatePlanet(DateTimeOffset now, long? parentId, SqliteTransaction transaction)
    {
        // Validate the cycle endpoint before persisting anything.
        _ = now.AddMonths(6);
        using var command = Command(transaction, """
            INSERT INTO planets(name,activated_at,seed,parent_id) VALUES('我的小星球',$now,$seed,$parent);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$seed", RandomNumberGenerator.GetInt32(1, int.MaxValue));
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    private Day ReadDay(string day, SqliteTransaction transaction)
    {
        using var query = Command(transaction, "SELECT focus_seconds,tokens,awarded FROM planet_days WHERE day=$day");
        query.Parameters.AddWithValue("$day", day);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new Day(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)) : new Day(0, 0, 0);
    }

    private SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static bool EligibleDay(string day, string firstDay, string today) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
        string.CompareOrdinal(day, firstDay) >= 0 && string.CompareOrdinal(day, today) <= 0;

    private static string LocalDay(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static InvalidOperationException NotInitialized() => new("请先初始化小星球。");
    private void CheckDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    private sealed record State(DateTimeOffset ActivatedAt, string ActivationDay, int FocusBaseline,
        long CurrentPlanetId, string? TokenSource, DateTimeOffset? TokenPeriodStart);
    private sealed record Planet(long Id, string Name, DateTimeOffset ActivatedAt, int Seed, bool HasChild);
    private readonly record struct Day(int FocusSeconds, long Tokens, int Awarded);
}
