using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Historical schema copied from v0.8.0 (bb2bf289f305f910959b5f245f28c18e6b954839),
// src/FocusGarden.Core/PlanetGrowthStore.cs. No current store is used to create
// the old database. All rows are synthetic and live in a dedicated test folder.
internal static class LegacyV1Fixture
{
    internal const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        PRAGMA foreign_keys=ON;
        CREATE TABLE planet_state (
            id INTEGER PRIMARY KEY CHECK(id=1),
            activated_at TEXT NOT NULL,
            activation_day TEXT NOT NULL,
            focus_baseline INTEGER NOT NULL CHECK(focus_baseline>=0),
            current_planet_id INTEGER NOT NULL,
            token_source TEXT,
            token_period_start TEXT
        );
        CREATE TABLE planets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            activated_at TEXT NOT NULL,
            seed INTEGER NOT NULL,
            parent_id INTEGER UNIQUE REFERENCES planets(id)
        );
        CREATE TABLE planet_days (
            day TEXT PRIMARY KEY,
            focus_seconds INTEGER NOT NULL DEFAULT 0 CHECK(focus_seconds>=0),
            tokens INTEGER NOT NULL DEFAULT 0 CHECK(tokens>=0),
            awarded INTEGER NOT NULL DEFAULT 0 CHECK(awarded>=0 AND awarded<=200)
        );
        CREATE TABLE planet_token_observations (
            day TEXT PRIMARY KEY,
            high_water INTEGER NOT NULL CHECK(high_water>=0)
        );
        CREATE TABLE planet_growth_credits (
            planet_id INTEGER NOT NULL REFERENCES planets(id),
            day TEXT NOT NULL,
            growth INTEGER NOT NULL CHECK(growth>=0 AND growth<=200),
            PRIMARY KEY(planet_id,day)
        );
        PRAGMA user_version=1;
        """;

    internal static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    internal static void Create(string path, DateTimeOffset now, string source)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = Schema; command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO planets(id,name,activated_at,seed,parent_id) VALUES
                (1,'第一家园',$activated,112233,NULL),(2,'当前家园',$now,445566,1);
            INSERT INTO planet_state VALUES(1,$activated,$activationDay,600,2,$source,$activated);
            INSERT INTO planet_days VALUES($yesterday,1500,100000,48),($today,18000,3100000,200);
            INSERT INTO planet_token_observations VALUES($yesterday,100000),($today,3100000);
            INSERT INTO planet_growth_credits VALUES(1,$yesterday,48),(1,$today,180),(2,$today,20);
            """;
        command.Parameters.AddWithValue("$activated", now.AddMonths(-6).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$activationDay", now.AddMonths(-6).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$today", now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$yesterday", now.AddDays(-1).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)));
        command.ExecuteNonQuery();
    }

    internal static string OriginalRows(SqliteConnection connection)
    {
        var result = new List<string>();
        foreach (var sql in new[] {
            "SELECT id,activated_at,activation_day,focus_baseline,current_planet_id,token_source,token_period_start FROM planet_state ORDER BY id",
            "SELECT id,name,activated_at,seed,parent_id FROM planets ORDER BY id",
            "SELECT day,focus_seconds,tokens,awarded FROM planet_days ORDER BY day",
            "SELECT day,high_water FROM planet_token_observations ORDER BY day",
            "SELECT planet_id,day,growth FROM planet_growth_credits ORDER BY planet_id,day",
            "SELECT name,seq FROM sqlite_sequence ORDER BY name" })
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); result.Add(sql);
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(JsonSerializer.Serialize(row));
            }
        }
        return string.Join("\n", result);
    }
}
