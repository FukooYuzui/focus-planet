using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FocusGarden.Core;

/// <summary>Preserve the last saved focus records before a new application version opens them.</summary>
public static class FocusDataProtection
{
    // Product naming and installation paths must never change this established data location.
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusGarden");

    public static string? PrepareForVersion(string dataDirectory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Version.TryParse(version, out var parsed)) throw new ArgumentException("应用版本无效。", nameof(version));
        version = parsed.ToString();
        dataDirectory = Path.GetFullPath(dataDirectory);
        var database = Path.Combine(dataDirectory, "focus.db");
        if (!File.Exists(database)) return null;
        using var source = Open(database, SqliteOpenMode.ReadOnly);
        CheckSchema(source);
        var markerPath = Path.Combine(dataDirectory, "focus-data-version.json");
        var backups = Path.Combine(dataDirectory, "backups");
        if (File.Exists(markerPath))
        {
            try
            {
                var marker = JsonSerializer.Deserialize<BackupMarker>(File.ReadAllText(markerPath));
                if (marker is not null && marker.Version == version && marker.BackupFile == Path.GetFileName(marker.BackupFile)
                    && !string.IsNullOrEmpty(marker.BackupFile) && File.Exists(Path.Combine(backups, marker.BackupFile))) return null;
            }
            catch (JsonException) { /* Recreate a missing or malformed bookkeeping marker from a new verified backup. */ }
        }

        Directory.CreateDirectory(backups);
        var fileName = $"focus-before-{version}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.db";
        var finalPath = Path.Combine(backups, fileName);
        var temporary = finalPath + ".partial";
        string expectedFingerprint;
        // A read transaction pins one consistent source snapshot, including committed WAL pages.
        using (var transaction = source.BeginTransaction(deferred: true))
        {
            RequireHealthy(source, transaction);
            expectedFingerprint = Fingerprint(source, transaction);
            using (File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var destination = Open(temporary, SqliteOpenMode.ReadWrite))
            {
                source.BackupDatabase(destination);
                RequireHealthy(destination);
                if (expectedFingerprint != Fingerprint(destination)) throw new IOException("专注记录备份校验失败，原记录未修改。");
                // Make the finished backup self-contained, with no required WAL sidecar.
                using var finish = destination.CreateCommand();
                finish.CommandText = "PRAGMA journal_mode=DELETE";
                if(!string.Equals(finish.ExecuteScalar() as string,"delete",StringComparison.OrdinalIgnoreCase))throw new IOException("无法完成独立专注备份，原记录未修改。");
            }
            transaction.Commit();
        }
        using(var reopened=Open(temporary,SqliteOpenMode.ReadOnly))
        {
            RequireHealthy(reopened);
            using var journal=reopened.CreateCommand();journal.CommandText="PRAGMA journal_mode";
            if(!string.Equals(journal.ExecuteScalar() as string,"delete",StringComparison.OrdinalIgnoreCase)||expectedFingerprint!=Fingerprint(reopened))throw new IOException("独立备份复查失败，原记录未修改。");
        }
        File.Move(temporary, finalPath);
        var markerTemporary = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(markerTemporary, JsonSerializer.Serialize(new BackupMarker(version, fileName)));
        File.Move(markerTemporary, markerPath, true);
        return finalPath;
    }

    internal static int CheckSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();command.CommandText = "PRAGMA user_version";
        long schema = (long)command.ExecuteScalar()!;
        if (schema is < 0 or > 1) throw new InvalidOperationException("专注记录由不兼容的版本创建，请使用对应的新版程序。原记录未重置。");
        return (int)schema;
    }
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, Mode=mode, Pooling=false, DefaultTimeout=5 }.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static void RequireHealthy(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="PRAGMA quick_check";
        if (!string.Equals(command.ExecuteScalar() as string,"ok",StringComparison.Ordinal)) throw new IOException("专注记录完整性检查未通过，原文件已保留，未继续启动。");
    }
    private static string Fingerprint(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sql in new[] { "SELECT * FROM sessions ORDER BY id", "SELECT * FROM session_days ORDER BY session_id,day", "SELECT * FROM settings ORDER BY id" })
        {
            using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;
            using var reader=command.ExecuteReader();
            hash.AppendData(Encoding.UTF8.GetBytes(sql));
            while(reader.Read())
            {
                var row=new object?[reader.FieldCount];
                for(int i=0;i<row.Length;i++) row[i]=reader.IsDBNull(i)?null:reader.GetValue(i);
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private sealed record BackupMarker(string Version, string BackupFile);
}
