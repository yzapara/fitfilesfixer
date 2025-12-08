using Dynastream.Fit;
using File = System.IO.File;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://*:5000"); // listens on port 5000 by default

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
            
            <button type='submit'>Upload & Fix</button>
        </form>
    </body>
    </html>";

    return Results.Content(html, "text/html; charset=utf-8");
});

// Serve a tiny upload page
// File processing endpoint
app.MapPost("/process", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("Expected multipart form.");

    var form = await request.ReadFormAsync();

    // ---- API KEY CHECK ----
    var apiKey = form["apiKey"].ToString();
    if (string.IsNullOrEmpty(apiKey) || apiKey != validApiKey)
    {
        var html = @"
        <html>
        <head><meta charset='utf-8'><title>Access Denied</title></head>
        <body style='font-family:sans-serif;'>
            <h2 style='color:red;'>Invalid API Key</h2>
            <p>The API key you entered is incorrect.</p>
            <p>Please go back and try again.</p>
            <a href='/'>&larr; Return to upload page</a>
        </body>
        </html>";

        return Results.Content(html, "text/html; charset=utf-8", statusCode: 401);
    }

    // ---- FILE CHECK ----
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

    // Save to temp
    var tmpDir = Path.Combine(Path.GetTempPath(), "fiteditor");
    Directory.CreateDirectory(tmpDir);
    var inputPath = Path.Combine(tmpDir, "activity.fit");
    var outputPath = Path.Combine(tmpDir, savedFileName);

    await using (var fs = File.Create(inputPath))
        await file.CopyToAsync(fs);

    // ---- PROCESS FIT FILE USING YOUR EXISTING LOGIC ----
    int nullCoords = 0;
    int outsideRegionCoords = 0;
    int fixedPoints = 0;
    int totalPoints = 0;
    try
    {
        var messages = ReadAllMessages(inputPath);

        const double MIN_LAT = 44.0;
        const double MAX_LAT = 53.0;
        const double MIN_LON = 22.0;
        const double MAX_LON = 41.0;

        double defaultLatDeg = 49.969490;
        double defaultLonDeg = 36.193652;

        int defaultLat = DegreesToSemicircles(defaultLatDeg);
        int defaultLon = DegreesToSemicircles(defaultLonDeg);
        Dynastream.Fit.DateTime? defaultTime = null;

        // PRE-PASS
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
                    if (ts != null)
                        defaultTime = ts;
                    break;
                }
            }
        }

        // MAIN PASS
        int? lastValidLat = null;
        int? lastValidLon = null;


        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            totalPoints++;
            if (string.Equals(m.Name, "record", StringComparison.OrdinalIgnoreCase))
            {
                var record = new RecordMesg(m);
                int? lat = record.GetPositionLat();
                int? lon = record.GetPositionLong();

                bool needsFix = false;

                if (lat == null || lon == null)
                {
                    nullCoords++;
                    needsFix = true;
                }

                if (!needsFix)
                {
                    double latDeg = SemicirclesToDegrees(lat!.Value);
                    double lonDeg = SemicirclesToDegrees(lon!.Value);

                    if (!IsInsideUkraine(latDeg, lonDeg, MIN_LAT, MAX_LAT, MIN_LON, MAX_LON))
                    {
                        needsFix = true;
                        outsideRegionCoords++;
                    }
                }

                if (needsFix)
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
                        if (defaultTime != null)
                            record.SetTimestamp(defaultTime);
                    }

                    fixedPoints++;
                }
                else
                {
                    lastValidLat = lat;
                    lastValidLon = lon;
                }

                messages[i] = record;
            }
        }

        WriteAllMessages(messages, outputPath);
    }
    catch (Exception ex)
    {
        return Results.Problem("Processing failed: " + ex.Message);
    }

    // RETURN THE OUTPUT FILE
    var downloadUrl = $"/download?file={Uri.EscapeDataString(savedFileName)}&name={Uri.EscapeDataString(outputName)}";

    var htmlSuccess = $@"
<html>
<head><meta charset='utf-8'><title>Success</title></head>
<body style='font-family: sans-serif;'>
    <h2>File processed successfully</h2>
    <p>Your file <b>{originalName}{extension}</b> has been fixed.</p>
    <p>Total number of points <b>{totalPoints}</b></p>
    <p>Points with null coordinates <b>{nullCoords}</b></p>
    <p>Points outside of UA <b>{outsideRegionCoords}</b></p>
    <p>Total number of fixed points <b>{fixedPoints}</b></p>
    <p><a href='{downloadUrl}' download>Click here to download: <b>{outputName}</b></a></p>
    <br/>
    <a href='/'>Upload another file</a>
</body>
</html>";

    return Results.Content(htmlSuccess, "text/html; charset=utf-8");
});

app.MapGet("/download", (HttpRequest request) =>
{
    // The name of the file *on disk* (e.g., activity_fixed.fit.GUID)
    var savedFileName = request.Query["file"].ToString();
    
    // The name the *user* should see (e.g., activity_fixed.fit)
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

// -----------------------------
// Helper methods (adapted from your console code)
// -----------------------------
static bool IsInsideUkraine(double latDeg, double lonDeg, double minLat, double maxLat, double minLon, double maxLon)
{
    return (latDeg >= minLat && latDeg <= maxLat) && (lonDeg >= minLon && lonDeg <= maxLon);
}

static int DegreesToSemicircles(double degrees)
{
    return (int)(degrees * (Math.Pow(2, 31) / 180.0));
}

static double SemicirclesToDegrees(int semicircles)
{
    return semicircles * (180.0 / Math.Pow(2, 31));
}

static List<Mesg> ReadAllMessages(string filePath)
{
    var allMessages = new List<Mesg>();
    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
    var decode = new Decode();
    var broadcaster = new MesgBroadcaster();

    broadcaster.MesgEvent += (sender, e) => allMessages.Add(e.mesg);
    decode.MesgEvent += broadcaster.OnMesg;
    decode.MesgDefinitionEvent += broadcaster.OnMesgDefinition;

    decode.Read(fs);
    return allMessages;
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