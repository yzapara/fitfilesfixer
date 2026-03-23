using Dynastream.Fit;
using File = System.IO.File;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://*:5000");

const string validApiKey = "RUSNI_PYZDA";
var app = builder.Build();

app.MapGet("/", () =>
{
    var html = @"
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

app.MapPost("/process", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("Expected multipart form.");

    var form = await request.ReadFormAsync();

    var apiKey = form["apiKey"].ToString();
    if (string.IsNullOrEmpty(apiKey) || apiKey != validApiKey)
    {
        var html = @"
        <html><head><meta charset='utf-8'><title>Access Denied</title></head>
        <body style='font-family:sans-serif;'>
            <h2 style='color:red;'>Invalid API Key</h2>
            <p>Please go back and try again.</p>
            <a href='/'>&larr; Return to upload page</a>
        </body></html>";
        return Results.Content(html, "text/html; charset=utf-8", statusCode: 401);
    }

    var file = form.Files["file"];
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");
    if (file.Length > 5 * 1024 * 1024)
        return Results.BadRequest("File too large. Max allowed is 5 MB.");

    var originalName = Path.GetFileNameWithoutExtension(file.FileName);
    var extension = Path.GetExtension(file.FileName);
    var uniqueId = Guid.NewGuid().ToString();
    var outputName = $"{originalName}_fixed{extension}";
    var savedFileName = $"{outputName}.{uniqueId}";

    var tmpDir = Path.Combine(Path.GetTempPath(), "fiteditor");
    Directory.CreateDirectory(tmpDir);
    var inputPath = Path.Combine(tmpDir, "activity.fit");
    var outputPath = Path.Combine(tmpDir, savedFileName);

    await using (var fs = File.Create(inputPath))
        await file.CopyToAsync(fs);

    int nullCoords = 0, outsideRegionCoords = 0, fixedPoints = 0, totalPoints = 0;
    int droppedTimestamp = 0, droppedDuplicate = 0, droppedCorrupt = 0;

    try
    {
        var messages = ReadAllMessages(inputPath);

        const double MIN_LAT = 44.0, MAX_LAT = 53.0;
        const double MIN_LON = 22.0, MAX_LON = 41.0;

        double defaultLatDeg = 49.969490, defaultLonDeg = 36.193652;
        int defaultLat = DegreesToSemicircles(defaultLatDeg);
        int defaultLon = DegreesToSemicircles(defaultLonDeg);
        Dynastream.Fit.DateTime? defaultTime = null;

        // FIT epoch: 1989-12-31. Valid activity window: ~2000 to 2035.
        // FIT timestamps are seconds since FIT epoch (1989-12-31 00:00:00 UTC).
        // 2000-01-01 = 315532800 - 631065600 = ... let's use absolute bounds:
        // Min: 2000-01-01 => FIT ts = 315532800 - 631065600? 
        // Actually FIT epoch offset from Unix: 631065600 seconds
        // Unix for 2000-01-01: 946684800. FIT ts = 946684800 - 631065600 = 315619200
        // Unix for 2035-01-01: 2051222400. FIT ts = 2051222400 - 631065600 = 1420156800
        const uint MIN_VALID_FIT_TS = 315619200u;  // 2000-01-01
        const uint MAX_VALID_FIT_TS = 1420156800u;  // 2035-01-01

        // Message nums that carry a timestamp field as field #253
        // and whose timestamp validity we want to enforce.
        // We'll apply timestamp filtering broadly to any message that has field 253.
        // Known-good non-activity message nums to always keep (no timestamp check):
        // file_id=0, capabilities=1, device_settings=2, user_profile=3, sport=12,
        // zones_target=7, hr_zone=8, power_zone=9, met_zone=10, file_creator=49,
        // event=21, device_info=23, workout=26, course=31, lap=19, session=18,
        // activity=34, length=101
        // For simplicity: drop any message whose timestamp field is present but out of range,
        // EXCEPT for the structural messages that don't have meaningful timestamps.

        // PRE-PASS: find first valid coordinate and timestamp inside Ukraine
        foreach (var m in messages)
        {
            if (!string.Equals(m.Name, "record", StringComparison.OrdinalIgnoreCase)) continue;
            var record = new RecordMesg(m);
            int? lat = record.GetPositionLat();
            int? lon = record.GetPositionLong();
            if (lat != null && lon != null)
            {
                double latDeg = SemicirclesToDegrees(lat.Value);
                double lonDeg = SemicirclesToDegrees(lon.Value);
                if (IsInsideUkraine(latDeg, lonDeg, MIN_LAT, MAX_LAT, MIN_LON, MAX_LON))
                {
                    defaultLat = lat.Value;
                    defaultLon = lon.Value;
                    var ts = record.GetTimestamp();
                    if (ts != null) defaultTime = ts;
                    break;
                }
            }
        }

        // MAIN PASS
        int? lastValidLat = null, lastValidLon = null;
        bool fileIdSeen = false;
        var cleanMessages = new List<Mesg>();

        foreach (var m in messages)
        {
            // --- 1. Drop duplicate file_id ---
            if (m.Num == MesgNum.FileId)
            {
                if (fileIdSeen) { droppedDuplicate++; continue; }
                fileIdSeen = true;
                cleanMessages.Add(m);
                continue;
            }

            // --- 2. Drop messages with no fields (completely corrupt / unknown local mesg) ---
            // The SDK creates a Mesg with Num=MesgNum.Invalid for unrecognised frames.
            if (m.Num == MesgNum.Invalid)
            {
                droppedCorrupt++;
                continue;
            }

            // --- 3. Timestamp validation for any message that carries field 253 ---
            var tsField = m.GetField(253); // 253 = timestamp field number in FIT
            if (tsField != null)
            {
                var rawVal = tsField.GetValue();
                if (rawVal is uint uval)
                {
                    if (uval < MIN_VALID_FIT_TS || uval > MAX_VALID_FIT_TS)
                    {
                        // For record messages, fix timestamp rather than drop entirely
                        // (we still want to fix coords). For non-record messages, drop.
                        if (!string.Equals(m.Name, "record", StringComparison.OrdinalIgnoreCase))
                        {
                            droppedTimestamp++;
                            continue;
                        }
                        // For records: fix the timestamp below along with coords
                    }
                }
            }

            // --- 4. Coordinate fixing for record messages ---
            if (string.Equals(m.Name, "record", StringComparison.OrdinalIgnoreCase))
            {
                totalPoints++;
                var record = new RecordMesg(m);
                int? lat = record.GetPositionLat();
                int? lon = record.GetPositionLong();

                bool needsFix = false;

                if (lat == null || lon == null)
                {
                    nullCoords++;
                    needsFix = true;
                }
                else
                {
                    double latDeg = SemicirclesToDegrees(lat.Value);
                    double lonDeg = SemicirclesToDegrees(lon.Value);
                    if (!IsInsideUkraine(latDeg, lonDeg, MIN_LAT, MAX_LAT, MIN_LON, MAX_LON))
                    {
                        outsideRegionCoords++;
                        needsFix = true;
                    }
                }

                // Also fix timestamp if it's out of range
                var recTsField = record.GetField(253);
                bool tsBad = false;
                if (recTsField != null && recTsField.GetValue() is uint rts)
                    tsBad = rts < MIN_VALID_FIT_TS || rts > MAX_VALID_FIT_TS;

                if (needsFix || tsBad)
                {
                    if (lastValidLat != null)
                    {
                        record.SetPositionLat(lastValidLat.Value);
                        record.SetPositionLong(lastValidLon!.Value);
                    }
                    else
                    {
                        record.SetPositionLat(defaultLat);
                        record.SetPositionLong(defaultLon);
                    }

                    if (tsBad && defaultTime != null)
                        record.SetTimestamp(defaultTime);

                    if (needsFix) fixedPoints++;
                    cleanMessages.Add(record);
                    continue;
                }
                else
                {
                    lastValidLat = lat;
                    lastValidLon = lon;
                }
            }

            cleanMessages.Add(m);
        }

        WriteAllMessages(cleanMessages, outputPath);
    }
    catch (Exception ex)
    {
        return Results.Problem("Processing failed: " + ex.Message);
    }

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
    <p><a href='{downloadUrl}' download>Download: <b>{outputName}</b></a></p>
    <br/><a href='/'>Upload another file</a>
</body>
</html>";

    return Results.Content(htmlSuccess, "text/html; charset=utf-8");
});

app.MapGet("/download", (HttpRequest request) =>
{
    var savedFileName = request.Query["file"].ToString();
    var userFileName = request.Query["name"].ToString();
    if (string.IsNullOrEmpty(savedFileName) || string.IsNullOrEmpty(userFileName))
        return Results.BadRequest("Missing file or name parameter.");

    var tmpDir = Path.Combine(Path.GetTempPath(), "fiteditor");
    var filePath = Path.Combine(tmpDir, savedFileName);
    if (!File.Exists(filePath))
        return Results.NotFound("File not found.");

    var stream = File.OpenRead(filePath);
    return Results.File(stream, "application/octet-stream", userFileName, enableRangeProcessing: false);
});

app.Run();

// ---- Helpers ----

static bool IsInsideUkraine(double lat, double lon,
    double minLat, double maxLat, double minLon, double maxLon)
    => lat >= minLat && lat <= maxLat && lon >= minLon && lon <= maxLon;

static int DegreesToSemicircles(double degrees)
    => (int)(degrees * (Math.Pow(2, 31) / 180.0));

static double SemicirclesToDegrees(int semicircles)
    => semicircles * (180.0 / Math.Pow(2, 31));

static List<Mesg> ReadAllMessages(string filePath)
{
    var all = new List<Mesg>();
    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
    var decode = new Decode();
    var broadcaster = new MesgBroadcaster();
    broadcaster.MesgEvent += (_, e) => all.Add(e.mesg);
    decode.MesgEvent += broadcaster.OnMesg;
    decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
    decode.Read(fs);
    return all;
}

static void WriteAllMessages(List<Mesg> messages, string filePath)
{
    using var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);
    var encoder = new Encode();
    encoder.Open(fs);
    foreach (var m in messages)
        encoder.Write(m);
    encoder.Close();
}