using Microsoft.Data.Sqlite;

namespace FitFilesFixer.Web.DataAccess;

public static class SqliteExtensions
{
    public static Dictionary<string, object?> QuerySingle(
        this SqliteConnection conn, string sql)
    {
        using var cmd    = conn.CreateCommand();
        cmd.CommandText  = sql;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new();
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    public static List<Dictionary<string, object?>> QueryRows(
        this SqliteConnection conn, string sql)
    {
        using var cmd    = conn.CreateCommand();
        cmd.CommandText  = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
