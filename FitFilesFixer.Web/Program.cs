using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Dynastream.Fit;
using Microsoft.Data.Sqlite;
using File = System.IO.File;

// ---------------------------------------------------------------------------
// Bootstrap
// ---------------------------------------------------------------------------
SQLitePCL.Batteries.Init(); // ensures native sqlite3 runtime is loaded on all platforms

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://*:5000");

builder.Services.AddHttpClient("geo", c =>
{
    c.BaseAddress = new Uri("http://ip-api.com");
    c.Timeout = TimeSpan.FromSeconds(3);
});

const string validApiKey = "RUSNI_PYZDA";
var app = builder.Build();

string GetConnectionString()
{
    var configured = app.Configuration.GetConnectionString("Sqlite");
    if (!string.IsNullOrEmpty(configured))
        return configured;

    // Fallback: resolve relative to content root so the path is always valid
    // regardless of working directory (avoids SQLite error 14 in dev)
    var defaultPath = Path.Combine(app.Environment.ContentRootPath, "data", "fitfiles.db");
    Directory.CreateDirectory(Path.GetDirectoryName(defaultPath)!);
    return $"Data Source={defaultPath}";
}

// ---------------------------------------------------------------------------
// GET /  — upload form
// ---------------------------------------------------------------------------
app.MapGet("/", () =>
{
    const string html = @"
    <html>
    <head><meta charset='utf-8'><title>FIT Fixer</title></head>
    <body style='font-family:sans-serif;'>
        <h2>Upload FIT file for processing</h2>
        <p>Service will try to remove jumps of track into points outside UA but keep sensors data</p>
        <form action='/process' method='post' enctype='multipart/form-data'>
            <label>API Key:</label><br/>
            <input type='text' name='apiKey' /><br/><br/>
            <label>Select FIT file:</label><br/>
            <input type='file' name='file' /><br/><br/>
            <button type='submit'>Upload &amp; Fix</button>
        </form>
    </body>
    </html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

// ---------------------------------------------------------------------------
// POST /process  — main processing endpoint
// ---------------------------------------------------------------------------
app.MapPost("/process", async (HttpRequest request, IHttpClientFactory httpClientFactory) =>
{
    var sw = Stopwatch.StartNew();

    if (!request.HasFormContentType)
        return Results.BadRequest("Expected multipart form.");

    var form = await request.ReadFormAsync();

    var clientIp = request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                   ?? request.HttpContext.Connection.RemoteIpAddress?.ToString()
                   ?? "unknown";

    // ---- API KEY CHECK ----
    var apiKey = form["apiKey"].ToString();
    if (string.IsNullOrEmpty(apiKey) || apiKey != validApiKey)
    {
        await Helpers.LogRequestAsync(GetConnectionString(), new RequestLog
        {
            Ip = clientIp,
            Success = false,
            ErrorMessage = "Invalid API key",
            ProcessingMs = (int)sw.ElapsedMilliseconds
        }, httpClientFactory);

        const string html = @"
        <html><head><meta charset='utf-8'><title>Access Denied</title></head>
        <body style='font-family:sans-serif;'>
            <h2 style='color:red;'>Invalid API Key</h2>
            <p>The API key you entered is incorrect.</p>
            <a href='/'>&larr; Return to upload page</a>
        </body></html>";
        return Results.Content(html, "text/html; charset=utf-8", statusCode: 401);
    }

    // ---- FILE CHECK ----
    var file = form.Files["file"];
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");
    if (file.Length > 5 * 1024 * 1024)
        return Results.BadRequest("File too large. Max allowed is 5 MB.");

    var originalName  = Path.GetFileNameWithoutExtension(file.FileName);
    var extension     = Path.GetExtension(file.FileName);
    var uniqueId      = Guid.NewGuid().ToString();
    var outputName    = $"{originalName}_fixed{extension}";
    var savedFileName = $"{outputName}.{uniqueId}";

    var tmpDir     = Path.Combine(Path.GetTempPath(), "fiteditor");
    Directory.CreateDirectory(tmpDir);
    var inputPath  = Path.Combine(tmpDir, "activity.fit");
    var outputPath = Path.Combine(tmpDir, savedFileName);

    await using (var fs = File.Create(inputPath))
        await file.CopyToAsync(fs);

    int nullCoords = 0, outsideRegionCoords = 0, fixedPoints = 0, totalPoints = 0;
    int droppedTimestamp = 0, droppedDuplicate = 0, droppedCorrupt = 0;

    try
    {
        var messages = FitHelpers.ReadAllMessages(inputPath);

        const double MIN_LAT = 44.0, MAX_LAT = 53.0;
        const double MIN_LON = 22.0, MAX_LON = 41.0;

        // FIT epoch offset from Unix: 631065600 s.  Valid window: 2000-01-01 → 2035-01-01
        const uint MIN_VALID_FIT_TS = 315619200u;
        const uint MAX_VALID_FIT_TS = 1420156800u;

        double defaultLatDeg = 49.969490, defaultLonDeg = 36.193652;
        int defaultLat = FitHelpers.DegreesToSemicircles(defaultLatDeg);
        int defaultLon = FitHelpers.DegreesToSemicircles(defaultLonDeg);
        Dynastream.Fit.DateTime? defaultTime = null;

        // PRE-PASS: find first valid Ukraine coordinate + timestamp
        foreach (var m in messages)
        {
            if (!string.Equals(m.Name, "record", StringComparison.OrdinalIgnoreCase)) continue;
            var record = new RecordMesg(m);
            int? lat = record.GetPositionLat();
            int? lon = record.GetPositionLong();
            if (lat == null || lon == null) continue;

            double latDeg = FitHelpers.SemicirclesToDegrees(lat.Value);
            double lonDeg = FitHelpers.SemicirclesToDegrees(lon.Value);
            if (FitHelpers.IsInsideUkraine(latDeg, lonDeg, MIN_LAT, MAX_LAT, MIN_LON, MAX_LON))
            {
                defaultLat = lat.Value;
                defaultLon = lon.Value;
                var ts = record.GetTimestamp();
                if (ts != null) defaultTime = ts;
                break;
            }
        }

        // MAIN PASS
        int? lastValidLat = null, lastValidLon = null;
        bool fileIdSeen = false;
        var cleanMessages = new List<Mesg>();

        foreach (var m in messages)
        {
            // 1. Deduplicate file_id
            if (m.Num == MesgNum.FileId)
            {
                if (fileIdSeen) { droppedDuplicate++; continue; }
                fileIdSeen = true;
                cleanMessages.Add(m);
                continue;
            }

            // 2. Drop completely corrupt / unknown messages
            if (m.Num == MesgNum.Invalid)
            {
                droppedCorrupt++;
                continue;
            }

            // 3. Timestamp validation for non-record messages carrying field 253
            if (!string.Equals(m.Name, "record", StringComparison.OrdinalIgnoreCase))
            {
                var tsField = m.GetField(253);
                if (tsField?.GetValue() is uint uval &&
                    (uval < MIN_VALID_FIT_TS || uval > MAX_VALID_FIT_TS))
                {
                    droppedTimestamp++;
                    continue;
                }
                cleanMessages.Add(m);
                continue;
            }

            // 4. Record message: fix coordinates and/or timestamp
            totalPoints++;
            var rec    = new RecordMesg(m);
            int? recLat = rec.GetPositionLat();
            int? recLon = rec.GetPositionLong();

            bool needsFix = false;
            if (recLat == null || recLon == null)
            {
                nullCoords++;
                needsFix = true;
            }
            else
            {
                double latDeg = FitHelpers.SemicirclesToDegrees(recLat.Value);
                double lonDeg = FitHelpers.SemicirclesToDegrees(recLon.Value);
                if (!FitHelpers.IsInsideUkraine(latDeg, lonDeg, MIN_LAT, MAX_LAT, MIN_LON, MAX_LON))
                {
                    outsideRegionCoords++;
                    needsFix = true;
                }
            }

            bool tsBad = rec.GetField(253)?.GetValue() is uint rts &&
                         (rts < MIN_VALID_FIT_TS || rts > MAX_VALID_FIT_TS);

            if (needsFix || tsBad)
            {
                if (lastValidLat != null)
                {
                    rec.SetPositionLat(lastValidLat.Value);
                    rec.SetPositionLong(lastValidLon!.Value);
                }
                else
                {
                    rec.SetPositionLat(defaultLat);
                    rec.SetPositionLong(defaultLon);
                }

                if (tsBad && defaultTime != null)
                    rec.SetTimestamp(defaultTime);

                if (needsFix) fixedPoints++;
            }
            else
            {
                lastValidLat = recLat;
                lastValidLon = recLon;
            }

            cleanMessages.Add(rec);
        }

        FitHelpers.WriteAllMessages(cleanMessages, outputPath);
    }
    catch (Exception ex)
    {
        await Helpers.LogRequestAsync(GetConnectionString(), new RequestLog
        {
            Ip = clientIp,
            FileName = file.FileName,
            FileSizeKb = (int)(file.Length / 1024),
            Success = false,
            ErrorMessage = ex.Message,
            ProcessingMs = (int)sw.ElapsedMilliseconds
        }, httpClientFactory);

        return Results.Problem("Processing failed: " + ex.Message);
    }

    sw.Stop();

    await Helpers.LogRequestAsync(GetConnectionString(), new RequestLog
    {
        Ip = clientIp,
        FileName = file.FileName,
        FileSizeKb = (int)(file.Length / 1024),
        TotalPoints = totalPoints,
        FixedPoints = fixedPoints,
        DroppedTimestamp = droppedTimestamp,
        DroppedDuplicate = droppedDuplicate,
        DroppedCorrupt = droppedCorrupt,
        ProcessingMs = (int)sw.ElapsedMilliseconds,
        Success = true
    }, httpClientFactory);

    var downloadUrl = $"/download?file={Uri.EscapeDataString(savedFileName)}&name={Uri.EscapeDataString(outputName)}";
    var htmlSuccess = $@"
<html>
<head><meta charset='utf-8'><title>Success</title></head>
<body style='font-family:sans-serif;'>
    <h2>File processed successfully</h2>
    <p>Your file <b>{originalName}{extension}</b> has been fixed.</p>
    <p>Total record points: <b>{totalPoints}</b></p>
    <p>Points with null coordinates: <b>{nullCoords}</b></p>
    <p>Points outside UA: <b>{outsideRegionCoords}</b></p>
    <p>Total fixed coordinate points: <b>{fixedPoints}</b></p>
    <p>Messages dropped (bad timestamp): <b>{droppedTimestamp}</b></p>
    <p>Messages dropped (duplicate file_id): <b>{droppedDuplicate}</b></p>
    <p>Messages dropped (corrupt/unknown): <b>{droppedCorrupt}</b></p>
    <p>Processing time: <b>{sw.ElapsedMilliseconds} ms</b></p>
    <p><a href='{downloadUrl}' download>Download: <b>{outputName}</b></a></p>
    <br/><a href='/'>Upload another file</a>
    &nbsp;|&nbsp;
    <a href='/stats'>View stats</a>
</body>
</html>";

    return Results.Content(htmlSuccess, "text/html; charset=utf-8");
});

// ---------------------------------------------------------------------------
// GET /download
// ---------------------------------------------------------------------------
app.MapGet("/download", (HttpRequest request) =>
{
    var savedFileName = request.Query["file"].ToString();
    var userFileName  = request.Query["name"].ToString();

    if (string.IsNullOrEmpty(savedFileName) || string.IsNullOrEmpty(userFileName))
        return Results.BadRequest("Missing file or name parameter.");

    var filePath = Path.Combine(Path.GetTempPath(), "fiteditor", savedFileName);
    if (!File.Exists(filePath))
        return Results.NotFound("File not found.");

    var stream = File.OpenRead(filePath);
    return Results.File(stream, "application/octet-stream", userFileName, enableRangeProcessing: false);
});
// ---------------------------------------------------------------------------
// GET /stats  — request history dashboard
// ---------------------------------------------------------------------------
app.MapGet("/stats", () =>
{
    using var conn = new SqliteConnection(GetConnectionString());
    conn.Open();

    var summary = conn.QuerySingle(@"
        SELECT
            COUNT(*)                                                    AS total,
            SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END)               AS succeeded,
            SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END)               AS failed,
            ROUND(AVG(CASE WHEN success = 1 THEN processing_ms END), 0) AS avg_ms,
            SUM(total_points)                                           AS sum_points,
            SUM(fixed_points)                                           AS sum_fixed
        FROM requests");

    var byCountry = conn.QueryRows(@"
        SELECT COALESCE(country, 'Unknown') AS country, COUNT(*) AS cnt
        FROM requests
        GROUP BY country
        ORDER BY cnt DESC
        LIMIT 20");

    var rows = conn.QueryRows(@"
        SELECT timestamp, ip, country, city, file_name, file_size_kb,
               total_points, fixed_points, processing_ms, success, error_message
        FROM requests
        ORDER BY id DESC
        LIMIT 50");

    static string N(object? v) => v?.ToString() ?? "-";
    static string Badge(object? v) =>
        v?.ToString() == "1"
            ? "<span style='color:green'>✓</span>"
            : "<span style='color:red'>✗</span>";

    var countryRows = string.Concat(byCountry.Select(r =>
        $"<tr><td>{N(r["country"])}</td><td>{N(r["cnt"])}</td></tr>"));

    var historyRows = string.Concat(rows.Select(r => $@"
        <tr>
            <td>{N(r["timestamp"])}</td>
            <td>{N(r["ip"])}</td>
            <td>{N(r["country"])}</td>
            <td>{N(r["city"])}</td>
            <td>{N(r["file_name"])}</td>
            <td>{N(r["file_size_kb"])} KB</td>
            <td>{N(r["total_points"])}</td>
            <td>{N(r["fixed_points"])}</td>
            <td>{N(r["processing_ms"])} ms</td>
            <td>{Badge(r["success"])}</td>
            <td style='color:red;font-size:0.85em'>{N(r["error_message"])}</td>
        </tr>"));

    var html = $@"
<html>
<head>
<meta charset='utf-8'>
<title>FIT Fixer — Stats</title>
<style>
  body  {{ font-family: sans-serif; padding: 20px; }}
  h2   {{ margin-top: 30px; }}
  table {{ border-collapse: collapse; width: 100%; margin-top: 10px; font-size: 0.9em; }}
  th, td {{ border: 1px solid #ccc; padding: 6px 10px; text-align: left; }}
  th   {{ background: #f0f0f0; }}
  tr:nth-child(even) {{ background: #fafafa; }}
  .kpi {{ display: inline-block; background:#f5f5f5; border:1px solid #ddd;
          border-radius:6px; padding:12px 24px; margin:6px; text-align:center; }}
  .kpi .val {{ font-size:2em; font-weight:bold; }}
  .kpi .lbl {{ font-size:0.8em; color:#666; }}
</style>
</head>
<body>
<h1>FIT Fixer — Request Statistics</h1>
<a href='/'>← Upload page</a>

<h2>Summary</h2>
<div>
  <div class='kpi'><div class='val'>{N(summary["total"])}</div><div class='lbl'>Total requests</div></div>
  <div class='kpi'><div class='val' style='color:green'>{N(summary["succeeded"])}</div><div class='lbl'>Succeeded</div></div>
  <div class='kpi'><div class='val' style='color:red'>{N(summary["failed"])}</div><div class='lbl'>Failed</div></div>
  <div class='kpi'><div class='val'>{N(summary["avg_ms"])} ms</div><div class='lbl'>Avg processing time</div></div>
  <div class='kpi'><div class='val'>{N(summary["sum_points"])}</div><div class='lbl'>Total points processed</div></div>
  <div class='kpi'><div class='val'>{N(summary["sum_fixed"])}</div><div class='lbl'>Total points fixed</div></div>
</div>

<h2>Requests by country</h2>
<table>
  <tr><th>Country</th><th>Requests</th></tr>
  {countryRows}
</table>

<h2>Last 50 requests</h2>
<table>
  <tr>
    <th>Time (UTC)</th><th>IP</th><th>Country</th><th>City</th>
    <th>File</th><th>Size</th><th>Points</th><th>Fixed</th>
    <th>Time</th><th>OK</th><th>Error</th>
  </tr>
  {historyRows}
</table>
</body>
</html>";

    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();

// ===========================================================================
// Type declarations — nothing but class/record definitions below this line
// ===========================================================================

// ---------------------------------------------------------------------------
// Request log data model
// ---------------------------------------------------------------------------
record RequestLog
{
    public string? Ip               { get; init; }
    public string? FileName         { get; init; }
    public int     FileSizeKb       { get; init; }
    public int     TotalPoints      { get; init; }
    public int     FixedPoints      { get; init; }
    public int     DroppedTimestamp { get; init; }
    public int     DroppedDuplicate { get; init; }
    public int     DroppedCorrupt   { get; init; }
    public int     ProcessingMs     { get; init; }
    public bool    Success          { get; init; }
    public string? ErrorMessage     { get; init; }
}

// ---------------------------------------------------------------------------
// Logging + geo lookup
// ---------------------------------------------------------------------------
static class Helpers
{
    public static async Task LogRequestAsync(
        string connectionString,
        RequestLog log,
        IHttpClientFactory httpClientFactory)
    {
        string? country = null, city = null;

        if (!string.IsNullOrEmpty(log.Ip) && log.Ip != "unknown" && !IsPrivateIp(log.Ip))
        {
            try
            {
                var geo  = httpClientFactory.CreateClient("geo");
                var resp = await geo.GetAsync($"/json/{log.Ip}?fields=country,city,status");
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var status) &&
                        status.GetString() == "success")
                    {
                        country = root.TryGetProperty("country", out var c)  ? c.GetString()  : null;
                        city    = root.TryGetProperty("city",    out var ci) ? ci.GetString() : null;
                    }
                }
            }
            catch
            {
                // Geo lookup is best-effort — never fail the request over it
            }
        }

        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO requests
                    (timestamp, ip, country, city, file_name, file_size_kb,
                     total_points, fixed_points,
                     dropped_timestamp, dropped_duplicate, dropped_corrupt,
                     processing_ms, success, error_message)
                VALUES
                    (@ts, @ip, @country, @city, @fn, @fsk,
                     @tp, @fp,
                     @dt, @dd, @dc,
                     @ms, @ok, @err)";

            cmd.Parameters.AddWithValue("@ts",      System.DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@ip",      (object?)log.Ip          ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", (object?)country          ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@city",    (object?)city              ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fn",      (object?)log.FileName     ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fsk",     log.FileSizeKb);
            cmd.Parameters.AddWithValue("@tp",      log.TotalPoints);
            cmd.Parameters.AddWithValue("@fp",      log.FixedPoints);
            cmd.Parameters.AddWithValue("@dt",      log.DroppedTimestamp);
            cmd.Parameters.AddWithValue("@dd",      log.DroppedDuplicate);
            cmd.Parameters.AddWithValue("@dc",      log.DroppedCorrupt);
            cmd.Parameters.AddWithValue("@ms",      log.ProcessingMs);
            cmd.Parameters.AddWithValue("@ok",      log.Success ? 1 : 0);
            cmd.Parameters.AddWithValue("@err",     (object?)log.ErrorMessage ?? DBNull.Value);

            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Logging must never crash the app
        }
    }

    public static bool IsPrivateIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return true;
        var b = addr.GetAddressBytes();
        return addr.ToString() == "::1"
            || addr.IsIPv6LinkLocal
            || (b.Length == 4 && (
                   b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)));
    }
}

// ---------------------------------------------------------------------------
// FIT file helpers
// ---------------------------------------------------------------------------
static class FitHelpers
{
    public static bool IsInsideUkraine(double lat, double lon,
        double minLat, double maxLat, double minLon, double maxLon)
        => lat >= minLat && lat <= maxLat && lon >= minLon && lon <= maxLon;

    public static int DegreesToSemicircles(double degrees)
        => (int)(degrees * (Math.Pow(2, 31) / 180.0));

    public static double SemicirclesToDegrees(int semicircles)
        => semicircles * (180.0 / Math.Pow(2, 31));

    public static List<Mesg> ReadAllMessages(string filePath)
    {
        var all         = new List<Mesg>();
        using var fs    = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var decode      = new Decode();
        var broadcaster = new MesgBroadcaster();
        broadcaster.MesgEvent      += (_, e) => all.Add(e.mesg);
        decode.MesgEvent           += broadcaster.OnMesg;
        decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        decode.Read(fs);
        return all;
    }

    public static void WriteAllMessages(List<Mesg> messages, string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);
        var encoder  = new Encode(ProtocolVersion.V20);
        encoder.Open(fs);
        foreach (var m in messages)
            encoder.Write(m);
        encoder.Close();
    }
}

// ---------------------------------------------------------------------------
// Minimal SQLite query helpers (avoids pulling in Dapper)
// ---------------------------------------------------------------------------
static class SqliteExtensions
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