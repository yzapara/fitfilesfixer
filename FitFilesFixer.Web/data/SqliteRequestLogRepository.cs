using FitFilesFixer.Web.Models;
using Microsoft.Data.Sqlite;

namespace FitFilesFixer.Web.Data;

public class SqliteRequestLogRepository : IRequestLogRepository
{
    public void EnsureSchema(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT,
                ip TEXT,
                country TEXT,
                city TEXT,
                file_name TEXT,
                saved_file_name TEXT,
                file_size_kb INTEGER,
                total_points INTEGER,
                fixed_points INTEGER,
                dropped_timestamp INTEGER,
                dropped_duplicate INTEGER,
                dropped_corrupt INTEGER,
                processing_ms INTEGER,
                success INTEGER,
                error_message TEXT
            );";
        cmd.ExecuteNonQuery();

        // Add column if older schema exists that lacks saved_file_name
        cmd.CommandText = "PRAGMA table_info(requests);";
        using var rdr = cmd.ExecuteReader();
        var hasSavedFileName = false;
        while (rdr.Read())
        {
            if (rdr.GetString(1) == "saved_file_name")
            {
                hasSavedFileName = true;
                break;
            }
        }

        if (!hasSavedFileName)
        {
            cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE requests ADD COLUMN saved_file_name TEXT;";
            cmd.ExecuteNonQuery();
        }
    }

    public void Add(string connectionString, RequestLog log)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO requests
                (timestamp, ip, country, city, file_name, saved_file_name, file_size_kb,
                 total_points, fixed_points,
                 dropped_timestamp, dropped_duplicate, dropped_corrupt,
                 processing_ms, success, error_message)
            VALUES
                (@ts, @ip, @country, @city, @fn, @sfn, @fsk,
                 @tp, @fp,
                 @dt, @dd, @dc,
                 @ms, @ok, @err)";
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@ip", (object?)log.Ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@country", (object?)log.Country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@city", (object?)log.City ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fn", (object?)log.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sfn", (object?)log.SavedFileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fsk", log.FileSizeKb);
        cmd.Parameters.AddWithValue("@tp", log.TotalPoints);
        cmd.Parameters.AddWithValue("@fp", log.FixedPoints);
        cmd.Parameters.AddWithValue("@dt", log.DroppedTimestamp);
        cmd.Parameters.AddWithValue("@dd", log.DroppedDuplicate);
        cmd.Parameters.AddWithValue("@dc", log.DroppedCorrupt);
        cmd.Parameters.AddWithValue("@ms", log.ProcessingMs);
        cmd.Parameters.AddWithValue("@ok", log.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@err", (object?)log.ErrorMessage ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, object?> GetSummary(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        return conn.QuerySingle(@"
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS succeeded,
                SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END) AS failed,
                ROUND(AVG(CASE WHEN success = 1 THEN processing_ms END), 0) AS avg_ms,
                SUM(total_points) AS sum_points,
                SUM(fixed_points) AS sum_fixed
            FROM requests");
    }

    public int GetUniqueIpCount(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        return Convert.ToInt32(conn.QuerySingle("SELECT COUNT(DISTINCT ip) AS cnt FROM requests")["cnt"] ?? 0);
    }

    public List<Dictionary<string, object?>> GetRequestsByCountry(string connectionString, int limit = 20)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn.QueryRows($@"
            SELECT COALESCE(country, 'Unknown') AS country, COUNT(*) AS cnt
            FROM requests
            GROUP BY country
            ORDER BY cnt DESC
            LIMIT {limit}");
    }

    public List<Dictionary<string, object?>> GetLastRequests(string connectionString, int limit = 50)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn.QueryRows($@"
            SELECT strftime('%d.%m.%Y %H:%M', timestamp) AS timestamp,
                    ip, country, city, file_name, saved_file_name, file_size_kb,
                    total_points, fixed_points, processing_ms, success, error_message
            FROM requests
            ORDER BY id DESC
            LIMIT {limit}");
    }
}
