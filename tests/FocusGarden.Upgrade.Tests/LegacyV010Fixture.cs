using Microsoft.Data.Sqlite;

// Historical fixture provenance: git tag v0.1.0, commit
// ef4403caf364179b820d015dfbed13f4954a5efd,
// src/FocusGarden.Core/FocusStore.cs constructor SQL, inspected with
// git show v0.1.0:src/FocusGarden.Core/FocusStore.cs.
// This intentionally does NOT use the current FocusStore to create an "old"
// database. The SQL schema below is copied from that historical constructor.
// All fixture records are synthetic; no real user data is read.
internal static class LegacyV010Fixture
{
    public const string SourceCommit = "ef4403caf364179b820d015dfbed13f4954a5efd";
    public const int TotalSeconds = 263;

    public const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        PRAGMA foreign_keys=ON;
        PRAGMA busy_timeout=5000;
        CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sessions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            task TEXT NOT NULL,
            started_at TEXT NOT NULL,
            ended_at TEXT,
            planned_seconds INTEGER NOT NULL CHECK(planned_seconds>0),
            elapsed_seconds INTEGER NOT NULL DEFAULT 0 CHECK(elapsed_seconds>=0 AND elapsed_seconds<=planned_seconds),
            status TEXT NOT NULL CHECK(status IN ('running','completed','emergency','interrupted')),
            last_checkpoint TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS one_running_session ON sessions(status) WHERE status='running';
        CREATE TABLE IF NOT EXISTS session_days (
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            day TEXT NOT NULL,
            seconds INTEGER NOT NULL CHECK(seconds>0),
            PRIMARY KEY(session_id,day)
        );
        PRAGMA user_version=1;
        """;

    public const string Records = """
        INSERT INTO settings(id,json) VALUES(1,'{"FocusMinutes":45,"BreakMinutes":7,"Immersive":false,"BlockedApps":["notepad"],"BlockedDomains":["example.com"]}');
        INSERT INTO sessions(id,task,started_at,ended_at,planned_seconds,elapsed_seconds,status,last_checkpoint) VALUES
          (1,'跨年完成｜合成记录','2025-12-31T23:59:20.0000000+08:00','2026-01-01T00:00:50.0000000+08:00',90,90,'completed','2026-01-01T00:00:50.0000000+08:00'),
          (3,'跨午夜提前结束｜合成记录','2026-02-28T23:59:50.0000000+08:00','2026-03-01T00:00:27.0000000+08:00',1500,37,'emergency','2026-03-01T00:00:27.0000000+08:00'),
          (7,'闰日中断｜合成记录','2024-02-28T23:59:00.0000000+08:00','2024-02-29T00:01:05.0000000+08:00',600,125,'interrupted','2024-02-29T00:01:05.0000000+08:00'),
          (42,'仍在进行｜合成记录','2026-10-27T10:00:00.0000000+08:00',NULL,1800,11,'running','2026-10-27T10:00:11.0000000+08:00');
        INSERT INTO session_days(session_id,day,seconds) VALUES
          (1,'2025-12-31',40),(1,'2026-01-01',50),
          (3,'2026-02-28',10),(3,'2026-03-01',27),
          (7,'2024-02-28',60),(7,'2024-02-29',65),
          (42,'2026-10-27',11);
        """;

    public static void Create(string dbPath)
    {
        using var connection = Open(dbPath);
        Execute(connection, Schema);
        Execute(connection, Records);
    }

    public static SqliteConnection Open(string dbPath, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
