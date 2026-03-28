using Microsoft.AspNetCore.HttpOverrides;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Dynastream.Fit;
using Microsoft.Data.Sqlite;
using FitFilesFixer.Web.Data;
using FitFilesFixer.Web.Infrastructure;
using FitFilesFixer.Web.Models;
using FitFilesFixer.Web.Services;
using File = System.IO.File;

// ---------------------------------------------------------------------------
// Bootstrap
// ---------------------------------------------------------------------------
SQLitePCL.Batteries.Init();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IRequestLogRepository, SqliteRequestLogRepository>();
builder.Services.AddSingleton<IRequestLogService, RequestLogService>();
builder.Services.AddSingleton<IGeolocationService, GeolocationService>();
builder.Services.AddSingleton<ICityLookupService, CityLookupService>();
builder.Services.AddSingleton<IFitFixerService, FitFixerService>();
builder.Services.AddSingleton<ILanguageService, LanguageService>();

builder.Services.Configure<ForwardedHeadersOptions>(options => {
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.WebHost.UseUrls("http://*:5000");

builder.Services.AddHttpClient("geo", c =>
{
    c.BaseAddress = new Uri("http://ip-api.com");
    c.Timeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddHttpClient("nominatim", c =>
{
    c.BaseAddress = new Uri("https://nominatim.openstreetmap.org");
    c.Timeout = TimeSpan.FromSeconds(5);
    // Nominatim usage policy requires a meaningful User-Agent identifying the app.
    c.DefaultRequestHeaders.UserAgent.ParseAdd("FitFilesFixer/1.0 (fitfilesfixer; contact@fitfilesfixer.com)");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

const string validApiKey = "RUSNI_PYZDA";
var app = builder.Build();
app.UseForwardedHeaders();

var requestLogRepo = app.Services.GetRequiredService<IRequestLogRepository>();
requestLogRepo.EnsureSchema(GetConnectionString());

string GetConnectionString()
{
    var configured = app.Configuration.GetConnectionString("Sqlite");
    if (!string.IsNullOrEmpty(configured))
        return configured;

    var defaultPath = Path.Combine(app.Environment.ContentRootPath, "data", "fitfiles.db");
    Directory.CreateDirectory(Path.GetDirectoryName(defaultPath)!);
    return $"Data Source={defaultPath}";
}

static void CleanupTempFiles(string directory, TimeSpan maxAge)
{
    try
    {
        if (!Directory.Exists(directory))
            return;

        var cutoff = System.DateTime.UtcNow - maxAge;
        foreach (var filePath in Directory.EnumerateFiles(directory))
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.LastWriteTimeUtc < cutoff)
                    fi.Delete();
            }
            catch { /* ignore file-level cleanup errors */ }
        }
    }
    catch { /* ignore cleanup errors */ }
}

// ---------------------------------------------------------------------------
// GET /api/geolocate  — returns best-guess city for the caller's IP
// ---------------------------------------------------------------------------
app.MapGet("/api/geolocate", async (HttpRequest request, IGeolocationService geoService) =>
{
    var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
    var geo = await geoService.GeolocateAsync(ip);
    return Results.Json(new { city = geo.City, lat = geo.Lat, lon = geo.Lon });
});

// ---------------------------------------------------------------------------
// GET /api/cities?q=...  — city autocomplete via Nominatim (OpenStreetMap)
// ---------------------------------------------------------------------------
app.MapGet("/api/cities", async (string? q, ICityLookupService cityLookup) =>
{
    var results = await cityLookup.SearchCitiesAsync(q ?? string.Empty);
    return Results.Json(results.Select(r => new { name = r.Name, lat = r.Latitude, lon = r.Longitude }));
});


app.MapGet("/", (HttpRequest request, HttpResponse response) =>
{
    var lang = Lang.Detect(request);
    Lang.SetCookie(response, lang);

    string T(string k) => Lang.T(k, lang);

    // Built as plain string concatenation — no $@"" escaping headaches with
    // double-quotes inside JS strings (city-item, selectCity, regex literals).
    var uploadScript =
        "<script>\n" +
        "var _a, _b;\n" +
        "function newCaptcha() {\n" +
        "  _a = Math.floor(Math.random() * 12) + 1;\n" +
        "  _b = Math.floor(Math.random() * 12) + 1;\n" +
        "  document.getElementById('captcha-q').textContent = _a + ' + ' + _b;\n" +
        "  document.getElementById('captcha-ans').value = '';\n" +
        "  document.getElementById('captcha-err').style.display = 'none';\n" +
        "}\n" +
        "newCaptcha();\n" +
        "function showFileName(input) {\n" +
        "  var el = document.getElementById('file-name');\n" +
        "  if (input.files && input.files[0]) { el.textContent = input.files[0].name; el.style.display = 'block'; }\n" +
        "}\n" +
        "var dz = document.getElementById('drop-zone');\n" +
        "dz.addEventListener('dragover', function(e) { e.preventDefault(); dz.style.background = '#f0f0f0'; });\n" +
        "dz.addEventListener('dragleave', function() { dz.style.background = ''; });\n" +
        "dz.addEventListener('drop', function(e) {\n" +
        "  e.preventDefault(); dz.style.background = '';\n" +
        "  var dt = new DataTransfer(); dt.items.add(e.dataTransfer.files[0]);\n" +
        "  var fi = document.getElementById('fit-file'); fi.files = dt.files; showFileName(fi);\n" +
        "});\n" +
        "var _cityDebounce = null, _currentSuggestions = [];\n" +
        "function onCityInput(val) {\n" +
        "  clearTimeout(_cityDebounce);\n" +
        "  if (val.length < 2) { hideSuggestions(); return; }\n" +
        "  _cityDebounce = setTimeout(function() {\n" +
        "    fetch('/api/cities?q=' + encodeURIComponent(val))\n" +
        "      .then(function(r) { return r.json(); }).then(showSuggestions).catch(function() {});\n" +
        "  }, 180);\n" +
        "}\n" +
        "function showSuggestions(cities) {\n" +
        "  var box = document.getElementById('citySuggestions');\n" +
        "  _currentSuggestions = cities;\n" +
        "  if (!cities.length) { box.style.display = 'none'; return; }\n" +
        "  box.innerHTML = cities.map(function(c, i) {\n" +
        "    return '<div class=\"city-item\" onmousedown=\"selectCity(' + i + ')\">' + escHtml(c.name) + '</div>';\n" +
        "  }).join('');\n" +
        "  box.style.display = 'block';\n" +
        "}\n" +
        "function selectCity(i) {\n" +
        "  var c = _currentSuggestions[i]; if (!c) return;\n" +
        "  document.getElementById('startCity').value = c.name;\n" +
        "  document.getElementById('startLat').value  = c.lat;\n" +
        "  document.getElementById('startLon').value  = c.lon;\n" +
        "  hideSuggestions();\n" +
        "}\n" +
        "function hideSuggestions() { setTimeout(function() { document.getElementById('citySuggestions').style.display = 'none'; }, 120); }\n" +
        "function escHtml(s) { return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }\n" +
        "(function() {\n" +
        "  fetch('/api/geolocate').then(function(r) { return r.json(); }).then(function(d) {\n" +
        "    if (d && d.city) {\n" +
        "      document.getElementById('startCity').value = d.city;\n" +
        "      document.getElementById('startLat').value  = d.lat;\n" +
        "      document.getElementById('startLon').value  = d.lon;\n" +
        "    }\n" +
        "  }).catch(function() {});\n" +
        "})();\n" +
        "function handleSubmit() {\n" +
        "  var ans = parseInt(document.getElementById('captcha-ans').value, 10);\n" +
        "  if (isNaN(ans) || ans !== _a + _b) {\n" +
        "    document.getElementById('captcha-err').style.display = 'block';\n" +
        "    newCaptcha(); return;\n" +
        "  }\n" +
        "  document.getElementById('upload-form').submit();\n" +
        "}\n" +
        "</script>";

    var html = $@"
<html>
<head><meta charset='utf-8'><title>{T("upload.title")}</title>
<style>{SharedCss.Css}</style></head>
<body>

<div class='page-header'>
  <div class='page-header-left'>
    <div class='icon-wrap'>
      <svg width='16' height='16' viewBox='0 0 16 16' fill='none'>
        <path d='M8 2v8M5 7l3 3 3-3M3 13h10' stroke='#111' stroke-width='1.4' stroke-linecap='round' stroke-linejoin='round'/>
      </svg>
    </div>
    <div>
      <h1>{T("upload.title")}</h1>
      <p class='sub'>{T("upload.sub")}</p>
    </div>
  </div>
  {Lang.LangToggleHtml(lang)}
</div>

<div class='tip'>
  <svg width='16' height='16' viewBox='0 0 16 16' fill='none' style='flex-shrink:0;margin-top:2px'>
    <circle cx='8' cy='8' r='7' stroke='#185fa5' stroke-width='1'/>
    <path d='M8 7v4M8 5v1' stroke='#185fa5' stroke-width='1.2' stroke-linecap='round'/>
  </svg>
  <div class='tip-text'>{T("upload.tip")}</div>
</div>

<p class='section-label'>{T("upload.section")}</p>

<form action='/process' method='post' enctype='multipart/form-data' id='upload-form'>
  <input type='hidden' name='apiKey' value='RUSNI_PYZDA' />
  <input type='hidden' name='lang' value='{lang}' />
  <input type='hidden' name='startLat' id='startLat' value=''>
  <input type='hidden' name='startLon' id='startLon' value=''>

  <div class='field'>
    <label>{T("upload.file_label")}</label>
    <div class='upload-area' id='drop-zone' onclick=""document.getElementById('fit-file').click()"">
      <svg width='24' height='24' viewBox='0 0 24 24' fill='none' style='margin-bottom:8px'>
        <path d='M12 3v12M8 11l4-4 4 4M4 17v2a2 2 0 002 2h12a2 2 0 002-2v-2' stroke='#888' stroke-width='1.4' stroke-linecap='round' stroke-linejoin='round'/>
      </svg>
      <div style='font-size:14px;color:#111'>{T("upload.click")}</div>
      <div class='file-hint'>{T("upload.hint")}</div>
      <div id='file-name'></div>
      <input type='file' id='fit-file' name='file' accept='.fit' style='display:none' onchange='showFileName(this)'>
    </div>
  </div>

  <div class='field'>
    <label>{T("upload.start_label")}</label>
    <div class='city-wrap'>
      <input type='text' id='startCity' placeholder='{T("upload.start_ph")}' autocomplete='off' oninput='onCityInput(this.value)' onblur='hideSuggestions()'>
      <div class='city-suggestions' id='citySuggestions'></div>
    </div>
    <div class='field-hint'>{T("upload.start_hint")}</div>
  </div>

  <div class='field'>
    <label>{T("upload.threshold_label")}</label>
    <div class='threshold-row'>
      <input type='number' id='threshold' name='threshold' value='70' min='5' max='100' step='1'>
      <span class='threshold-unit'>{T("upload.threshold_unit")}</span>
      <span class='threshold-hint'>{T("upload.threshold_hint")}</span>
    </div>
  </div>

  <div class='field'>
    <label>{T("upload.captcha_label")}</label>
    <div class='captcha-row'>
      <div class='captcha-q' id='captcha-q'></div>
      <span style='font-size:14px;color:#888'>{T("upload.captcha_eq")}</span>
      <input type='text' id='captcha-ans' placeholder='{T("upload.captcha_ph")}' inputmode='numeric'>
    </div>
    <div class='err' id='captcha-err'>{T("upload.captcha_err")}</div>
  </div>

  <div class='actions'>
    <button type='button' class='btn-primary' onclick='handleSubmit()'>{T("upload.submit")}</button>
  </div>
</form>

{uploadScript}

{SharedCss.Footer(lang)}
</body></html>";

    return Results.Content(html, "text/html; charset=utf-8");
});

// ---------------------------------------------------------------------------
// POST /process  — main processing endpoint
// ---------------------------------------------------------------------------
app.MapPost("/process", async (HttpRequest request, HttpResponse response, IFitFixerService fitFixerService, IRequestLogService requestLogService, ILanguageService langService) =>
{
    var sw = Stopwatch.StartNew();

    if (!request.HasFormContentType)
        return Results.BadRequest("Expected multipart form.");

    var form = await request.ReadFormAsync();
    var lang = form["lang"].ToString();
    if (lang != "uk" && lang != "en") lang = langService.Detect(request);
    langService.SetCookie(response, lang);

    string T(string k) => langService.T(k, lang);

    var clientIp = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // ---- SPEED LIMIT PARAMETER ----
    // Maximum plausible speed in km/h. Any point that would require exceeding
    // this speed from the previous valid point is treated as corrupt.
    double maxSpeedKmh = 70.0;
    if (double.TryParse(form["threshold"].ToString(), out var parsedSpeed))
        maxSpeedKmh = Math.Clamp(parsedSpeed, 5, 100);
    double maxSpeedMs = maxSpeedKmh / 3.6;

    // ---- START AREA PARAMETER ----
    double? startLat = null, startLon = null;
    if (double.TryParse(form["startLat"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sLat) &&
        double.TryParse(form["startLon"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sLon) &&
        sLat >= -90 && sLat <= 90 && sLon >= -180 && sLon <= 180)
    {
        startLat = sLat;
        startLon = sLon;
    }

    // ---- API KEY CHECK ----
    var apiKey = form["apiKey"].ToString();
    if (string.IsNullOrEmpty(apiKey) || apiKey != validApiKey)
    {
        await requestLogService.LogAsync(GetConnectionString(), new RequestLog
        {
            Ip = clientIp,
            Success = false,
            ErrorMessage = "Invalid API key",
            ProcessingMs = (int)sw.ElapsedMilliseconds
        });

        var deniedHtml = $@"
<html><head><meta charset='utf-8'><title>{T("denied.title")}</title>
<style>{SharedCss.Css}</style></head>
<body>
<div class='page-header'>
  <div class='page-header-left'>
    <div class='icon-wrap'>
      <svg width='16' height='16' viewBox='0 0 16 16' fill='none'>
        <path d='M8 2v8M5 7l3 3 3-3M3 13h10' stroke='#111' stroke-width='1.4' stroke-linecap='round' stroke-linejoin='round'/>
      </svg>
    </div>
    <div><h1>FIT Fixer</h1></div>
  </div>
  {Lang.LangToggleHtml(lang)}
</div>
<h2 style='color:red'>{T("denied.heading")}</h2>
<p>{T("denied.body")}</p>
<a href='/?lang={lang}'>{T("denied.back")}</a>
{SharedCss.Footer(lang)}
</body></html>";
        return Results.Content(deniedHtml, "text/html; charset=utf-8", statusCode: 401);
    }

    // ---- FILE CHECK ----
    var file = form.Files["file"];
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");
    if (file.Length > 5 * 1024 * 1024)
        return Results.BadRequest("File too large. Max allowed is 5 MB.");

    var originalName  = Path.GetFileNameWithoutExtension(file.FileName);
    var extension     = Path.GetExtension(file.FileName);

    var tmpDir = Path.Combine(Path.GetTempPath(), "fiteditor");
    CleanupTempFiles(tmpDir, TimeSpan.FromDays(7));
    Directory.CreateDirectory(tmpDir);
    var uploadId = Guid.NewGuid().ToString("N");
    var savedUploadName = $"upload_{uploadId}{extension}";
    var savedUploadPath = Path.Combine(tmpDir, savedUploadName);

    // Save file to temp for potential failed download
    await using (var sourceStream = file.OpenReadStream())
    await using (var targetStream = File.Create(savedUploadPath))
    {
        await sourceStream.CopyToAsync(targetStream);
    }

    // Read file into memory for processing to avoid file locks
    using var memoryStream = new MemoryStream();
    await using (var readStream = File.OpenRead(savedUploadPath))
    {
        await readStream.CopyToAsync(memoryStream);
    }
    memoryStream.Position = 0;

    FitProcessResult result;

    try
    {
        result = await fitFixerService.ProcessAsync(memoryStream, file.FileName, maxSpeedKmh, startLat, startLon);

        await requestLogService.LogAsync(GetConnectionString(), new RequestLog
        {
            Ip = clientIp,
            FileName = file.FileName,
            FileSizeKb = (int)(file.Length / 1024),
            TotalPoints = result.TotalPoints,
            FixedPoints = result.FixedPoints,
            DroppedTimestamp = result.DroppedTimestamp,
            DroppedDuplicate = result.DroppedDuplicate,
            DroppedCorrupt = result.DroppedCorrupt,
            Success = true,
            ProcessingMs = (int)sw.ElapsedMilliseconds
        });

        // Delete the uploaded file after successful processing
        File.Delete(savedUploadPath);
    }
    catch (Exception ex)
    {
        await requestLogService.LogAsync(GetConnectionString(), new RequestLog
        {
            Ip = clientIp,
            FileName = file.FileName,
            SavedFileName = savedUploadName,
            FileSizeKb = (int)(file.Length / 1024),
            Success = false,
            ErrorMessage = ex.Message,
            ProcessingMs = (int)sw.ElapsedMilliseconds
        });

        var errorHtml = $@"
<html>
<head>
<meta charset='utf-8'>
<title>FIT Fixer — Error</title>
<style>{SharedCss.Css}</style>
</head>
<body>

<div class='page-header'>
  <div class='page-header-left'>
    <div class='icon-wrap'>
      <svg width='16' height='16' viewBox='0 0 16 16' fill='none'>
        <path d='M8 2v8M5 7l3 3 3-3M3 13h10' stroke='#111' stroke-width='1.4' stroke-linecap='round' stroke-linejoin='round'/>
      </svg>
    </div>
    <div><h1>FIT Fixer</h1></div>
  </div>
  {Lang.LangToggleHtml(lang)}
</div>

<h2 style='color:red'>{T("result.error_heading")}</h2>
<p>{T("result.error_body")}</p>
<ul>
  <li>{T("stats.col_file")}: {file.FileName}</li>
  <li>{T("stats.col_size")}: {(file.Length / 1024)} KB</li>
  <li>{T("result.error_message")}: {WebUtility.HtmlEncode(ex.Message)}</li>
</ul>
<p>
  <a class='btn-primary' href='/download-original?file={Uri.EscapeDataString(savedUploadName)}&name={Uri.EscapeDataString(file.FileName)}'>{T("result.download_original")}</a>
  <a class='btn-secondary' href='/?lang={lang}'>{T("result.upload_another")}</a>
  <a class='btn-secondary' href='/stats?lang={lang}'>{T("upload.stats_link")}</a>
</p>
{SharedCss.Footer(lang)}
</body>
</html>";

        return Results.Content(errorHtml, "text/html; charset=utf-8", statusCode: 500);
    }

    sw.Stop();

    var outputName = result.OutputName;
    var savedFileName = Path.GetFileName(result.OutputPath);

    var totalPoints = result.TotalPoints;
    var nullCoords = result.NullCoords;
    var jumpCoords = result.JumpCoords;
    var fixedPoints = result.FixedPoints;
    var droppedTimestamp = result.DroppedTimestamp;
    var droppedDuplicate = result.DroppedDuplicate;
    var droppedCorrupt = result.DroppedCorrupt;

    var mapPoints = result.TrackPoints;
    var trackJson = "[" + string.Join(",", mapPoints.Select(p => $"[{p.Lat:F6},{p.Lon:F6},{(p.Fixed ? 1 : 0)}]")) + "]";

    var downloadUrl = $"/download?file={Uri.EscapeDataString(savedFileName)}&name={Uri.EscapeDataString(outputName)}&lang={lang}";

    var mapSection = "";
    if (mapPoints.Count > 0)
    {
        // Resolve localised labels before embedding in JS — avoids any
        // quote-escaping issues inside the $@"..." interpolated string.
        var lblStart  = T("result.map_start");
        var lblEnd    = T("result.map_end");
        var lblOk     = T("result.map_ok");
        var lblFixed  = T("result.map_fixed");
        var lblTitle  = T("result.map_section");

        // The JS is built as a plain $"..." (non-verbatim) string so we can
        // use \n for newlines and avoid all {{ }} escaping entirely.
        // Single quotes are used for all HTML attribute values inside JS strings.
        var mapScript = "<script>\n" +
            "(function(){\n" +
            $"  var pts = {trackJson};\n" +
            $"  var lblOk = '{lblOk}', lblFixed = '{lblFixed}';\n" +
            "  if (!pts.length) return;\n" +
            "  var map = L.map('map');\n" +
            "  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {\n" +
            "    attribution: '\\u00a9 <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a>',\n" +
            "    maxZoom: 19\n" +
            "  }).addTo(map);\n" +
            "  var segments = [], cur = null;\n" +
            "  for (var i = 0; i < pts.length; i++) {\n" +
            "    var isFixed = pts[i][2] === 1;\n" +
            "    if (!cur || cur.fixed !== isFixed) { cur = { fixed: isFixed, coords: [] }; segments.push(cur); }\n" +
            "    cur.coords.push([pts[i][0], pts[i][1]]);\n" +
            "    if (segments.length > 1 && cur.coords.length === 1) {\n" +
            "      var prev = segments[segments.length - 2];\n" +
            "      cur.coords.unshift(prev.coords[prev.coords.length - 1]);\n" +
            "    }\n" +
            "  }\n" +
            "  var allCoords = pts.map(function(p){ return [p[0], p[1]]; });\n" +
            "  var bounds = L.latLngBounds(allCoords);\n" +
            "  var hasFixed = false;\n" +
            "  segments.forEach(function(seg) {\n" +
            "    if (seg.fixed) hasFixed = true;\n" +
            "    L.polyline(seg.coords, {\n" +
            "      color:   seg.fixed ? '#e53e3e' : '#2563eb',\n" +
            "      weight:  3,\n" +
            "      opacity: seg.fixed ? 0.9 : 0.75\n" +
            "    }).addTo(map);\n" +
            "  });\n" +
            "  var legend = document.getElementById('map-legend');\n" +
            "  if (legend) {\n" +
            "    legend.innerHTML =\n" +
            "      '<span class=\"legend-item\"><span class=\"legend-dot ok\"></span>' + lblOk + '</span>' +\n" +
            "      (hasFixed ? ' <span class=\"legend-item\"><span class=\"legend-dot fixed\"></span>' + lblFixed + '</span>' : '');\n" +
            "  }\n" +
            "  var first = pts[0], last = pts[pts.length - 1];\n" +
            "  var pin = 'width:12px;height:12px;border-radius:50%;border:2px solid #fff;box-shadow:0 1px 3px rgba(0,0,0,.4)';\n" +
            $"  L.marker([first[0],first[1]], {{icon:L.divIcon({{html:'<div style=\"'+pin+';background:#16a34a\"></div>',className:'',iconAnchor:[6,6]}})}}).bindTooltip('{lblStart}').addTo(map);\n" +
            $"  L.marker([last[0],last[1]],   {{icon:L.divIcon({{html:'<div style=\"'+pin+';background:#dc2626\"></div>',className:'',iconAnchor:[6,6]}})}}).bindTooltip('{lblEnd}').addTo(map);\n" +
            "  map.fitBounds(bounds, { padding: [24, 24] });\n" +
            "})();\n" +
            "</script>";

        mapSection =
            $"<p class='section-label'>{lblTitle}</p>\n" +
            "<div id='map'></div>\n" +
            "<div class='map-legend' id='map-legend'></div>\n" +
            "<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>\n" +
            "<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>\n" +
            mapScript;
    }

    var htmlSuccess = $@"
<html>
<head>
<meta charset='utf-8'>
<title>FIT Fixer — Done</title>
<style>{SharedCss.Css}</style>
</head>
<body>

<div class='page-header'>
  <div class='page-header-left'>
    <div class='icon-ok'>
      <svg width='16' height='16' viewBox='0 0 16 16' fill='none'>
        <path d='M3 8l3.5 3.5L13 5' stroke='#1a7f4b' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'/>
      </svg>
    </div>
    <div>
      <h1>{originalName}{extension}</h1>
      <p class='sub'>{string.Format(T("result.sub"), sw.ElapsedMilliseconds)}</p>
    </div>
  </div>
  {langService.LangToggleHtml(lang)}
</div>

<p class='section-label'>{T("result.dropped")}</p>
<div class='drop-row'>
  <div class='drop-badge'>{string.Format(T("result.bad_ts"), droppedTimestamp)}</div>
  <div class='drop-badge'>{string.Format(T("result.dup"), droppedDuplicate)}</div>
  <div class='drop-badge'>{string.Format(T("result.corrupt"), droppedCorrupt)}</div>
</div>

{mapSection}

<div class='tip'>
  <svg width='16' height='16' viewBox='0 0 16 16' fill='none'>
    <circle cx='8' cy='8' r='7' stroke='#185fa5' stroke-width='1'/>
    <path d='M8 7v4M8 5v1' stroke='#185fa5' stroke-width='1.2' stroke-linecap='round'/>
  </svg>
  <div class='tip-text'>{T("result.tip")}</div>
</div>

<div class='actions'>
  <a class='btn-primary' href='{downloadUrl}' download>{string.Format(T("result.download"), outputName)}</a>
  <a class='btn-secondary' href='/?lang={lang}'>{T("result.upload_another")}</a>
</div>

{SharedCss.Footer(lang)}
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

    var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.DeleteOnClose);
    return Results.File(stream, "application/octet-stream", userFileName, enableRangeProcessing: false);
});

app.MapGet("/download-original", (HttpRequest request) =>
{
    var savedFileName = request.Query["file"].ToString();
    var userFileName = request.Query["name"].ToString();

    if (string.IsNullOrEmpty(savedFileName) || string.IsNullOrEmpty(userFileName))
        return Results.BadRequest("Missing file or name parameter.");

    if (savedFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest("Invalid file name.");

    var filePath = Path.Combine(Path.GetTempPath(), "fiteditor", savedFileName);
    if (!File.Exists(filePath))
        return Results.NotFound("File not found.");

    var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.DeleteOnClose);
    return Results.File(stream, "application/octet-stream", userFileName, enableRangeProcessing: false);
});

// ---------------------------------------------------------------------------
// GET /stats  — request history dashboard
// ---------------------------------------------------------------------------
app.MapGet("/stats", (HttpRequest request, HttpResponse response, ILanguageService langService, IRequestLogRepository requestLogRepository) =>
{
    var lang = langService.Detect(request);
    langService.SetCookie(response, lang);
    string T(string k) => langService.T(k, lang);

    var summary = requestLogRepository.GetSummary(GetConnectionString());
    var uniqueIps = new Dictionary<string, object?> { ["cnt"] = requestLogRepository.GetUniqueIpCount(GetConnectionString()) };
    var byCountry = requestLogRepository.GetRequestsByCountry(GetConnectionString());
    var rows = requestLogRepository.GetLastRequests(GetConnectionString());

    static string N(object? v) => v?.ToString() ?? "-";
    static string Badge(object? v) =>
        v?.ToString() == "1"
            ? "<span style='color:green'>✓</span>"
            : "<span style='color:red'>✗</span>";

    var countryRows = string.Concat(byCountry.Select(r =>
        $"<tr><td>{N(r["country"])}</td><td>{N(r["cnt"])}</td></tr>"));

    var historyRows = string.Concat(rows.Select(r => {
        var saved = N(r["saved_file_name"]);
        var fileName = N(r["file_name"]);
        var success = N(r["success"]) == "1";
        var downloadLink = !success && !string.IsNullOrEmpty(saved) && saved != "-"
            ? $"<a href='/download-original?file={Uri.EscapeDataString(saved)}&name={Uri.EscapeDataString(fileName)}'>{T("stats.col_download")}</a>"
            : fileName;

        return $@"
        <tr>
            <td>{N(r["timestamp"])}</td>
            <td>{N(r["ip"])}</td>
            <td>{N(r["country"])}</td>
            <td>{N(r["city"])}</td>
            <td>{downloadLink}</td>
            <td>{N(r["file_size_kb"])} KB</td>
            <td>{N(r["total_points"])}</td>
            <td>{N(r["fixed_points"])}</td>
            <td>{N(r["processing_ms"])} ms</td>
            <td>{Badge(r["success"])}</td>
            <td style='color:red;font-size:0.85em'>{N(r["error_message"])}</td>
        </tr>";
    }));

    var html = $@"
<html>
<head>
<meta charset='utf-8'>
<title>{T("stats.title")}</title>
<style>{SharedCss.Css}</style>
</head>
<body>

<div class='page-header'>
  <div class='page-header-left'>
    <div class='icon-wrap'>
      <svg width='16' height='16' viewBox='0 0 16 16' fill='none'>
        <path d='M8 2v8M5 7l3 3 3-3M3 13h10' stroke='#111' stroke-width='1.4' stroke-linecap='round' stroke-linejoin='round'/>
      </svg>
    </div>
    <div><h1>FIT Fixer — {T("stats.heading")}</h1></div>
  </div>
  {Lang.LangToggleHtml(lang)}
</div>

<p style='margin-bottom:20px'><a href='/?lang={lang}' style='color:#666;font-size:13px;text-decoration:none'>{T("stats.back")}</a></p>

<p class='section-label'>{T("stats.summary")}</p>
<div>
  <div class='stat-kpi'><div class='val'>{N(summary["total"])}</div><div class='lbl'>{T("stats.total")}</div></div>
  <div class='stat-kpi'><div class='val' style='color:green'>{N(summary["succeeded"])}</div><div class='lbl'>{T("stats.succeeded")}</div></div>
  <div class='stat-kpi'><div class='val' style='color:red'>{N(summary["failed"])}</div><div class='lbl'>{T("stats.failed")}</div></div>
  <div class='stat-kpi'><div class='val'>{N(summary["avg_ms"])} ms</div><div class='lbl'>{T("stats.avg_ms")}</div></div>
  <div class='stat-kpi'><div class='val'>{N(summary["sum_points"])}</div><div class='lbl'>{T("stats.sum_points")}</div></div>
  <div class='stat-kpi'><div class='val'>{N(summary["sum_fixed"])}</div><div class='lbl'>{T("stats.sum_fixed")}</div></div>
  <div class='stat-kpi'><div class='val'>{N(uniqueIps["cnt"])}</div><div class='lbl'>{T("stats.unique_ips")}</div></div>
</div>

<hr/>

<p class='section-label'>{T("stats.by_country")}</p>
<table>
  <tr><th>{T("stats.country")}</th><th>{T("stats.requests")}</th></tr>
  {countryRows}
</table>

<hr/>

<p class='section-label'>{T("stats.last50")}</p>
<table>
  <tr>
    <th>{T("stats.col_time")}</th><th>{T("stats.col_ip")}</th>
    <th>{T("stats.col_country")}</th><th>{T("stats.col_city")}</th>
    <th>{T("stats.col_file")}</th><th>{T("stats.col_size")}</th><th>{T("stats.col_download")}</th>
    <th>{T("stats.col_points")}</th><th>{T("stats.col_fixed")}</th>
    <th>{T("stats.col_time2")}</th><th>{T("stats.col_ok")}</th>
    <th>{T("stats.col_error")}</th>
  </tr>
  {historyRows}
</table>

{SharedCss.Footer(lang)}
</body>
</html>";

    return Results.Content(html, "text/html; charset=utf-8");
});

// ---------------------------------------------------------------------------
// POST /admin/cleanup-ipv6  — one-off maintenance helper
// ---------------------------------------------------------------------------
app.MapPost("/admin/cleanup-ipv6", (HttpRequest request) =>
{
    using var conn = new SqliteConnection(GetConnectionString());
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"DELETE FROM requests WHERE ip LIKE '::ffff:%';";
    var count = cmd.ExecuteNonQuery();
    return Results.Ok($"Deleted {count} rows with ::ffff: prefix");
});

app.Run();

// ===========================================================================
// Type declarations
// ===========================================================================

// ---------------------------------------------------------------------------
// Shared CSS
// ---------------------------------------------------------------------------
static class SharedCss
{
    public static string Footer(string lang) => lang == "uk"
        ? @"<div class='donate-footer'>
  <a class='donate-banner' href='https://defensivewave.org/' target='_blank' rel='noopener'>
    <div class='donate-banner-left'>
      <div class='donate-flag'>🇺🇦</div>
      <div class='donate-text'>
        <strong>Підтримай захист України</strong>
        <span>Задонать на українську армію через Defensive Wave</span>
      </div>
    </div>
    <div class='donate-btn'>Задонатити →</div>
  </a>
</div>"
        : @"<div class='donate-footer'>
  <a class='donate-banner' href='https://defensivewave.org/' target='_blank' rel='noopener'>
    <div class='donate-banner-left'>
      <div class='donate-flag'>🇺🇦</div>
      <div class='donate-text'>
        <strong>Support Ukraine's Defence</strong>
        <span>Donate to the Ukrainian army via Defensive Wave</span>
      </div>
    </div>
    <div class='donate-btn'>Donate now →</div>
  </a>
</div>";

    public const string Css = @"
  body { font-family: sans-serif; padding: 32px 24px; max-width: 760px; margin: 0 auto; color: #111; background: #fff; }
  h1 { font-size: 20px; font-weight: 500; margin: 0 0 4px; }
  .sub { font-size: 13px; color: #666; margin: 0 0 28px; }
  .section-label { font-size: 11px; font-weight: 500; color: #888; text-transform: uppercase; letter-spacing: 0.05em; margin: 0 0 8px; }
  hr { border: none; border-top: 1px solid #eee; margin: 20px 0; }
  .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin: 0 0 24px; }
  .kpi { background: #f5f5f5; border-radius: 8px; padding: 12px 16px; }
  .kpi .val { font-size: 22px; font-weight: 500; }
  .kpi .val.good { color: #1a7f4b; }
  .kpi .lbl { font-size: 12px; color: #666; margin-top: 2px; }
  .drop-row { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 24px; }
  .drop-badge { background: #fdecea; color: #a32d2d; border-radius: 6px; padding: 5px 12px; font-size: 13px; }
  .tip { background: #e8f0fb; border: 1px solid #b5d4f4; border-radius: 10px; padding: 14px 18px; display: flex; gap: 12px; align-items: flex-start; margin-bottom: 24px; }
  .tip-text { font-size: 14px; color: #185fa5; line-height: 1.6; }
  .tip-text a { color: #185fa5; font-weight: 500; }
  .actions { display: flex; gap: 10px; flex-wrap: wrap; align-items: center; }
  .btn-primary { background: #111; color: #fff; border: none; border-radius: 8px; padding: 10px 20px; font-size: 14px; font-weight: 500; cursor: pointer; text-decoration: none; display: inline-block; }
  .btn-primary:hover { opacity: 0.85; }
  .btn-secondary { background: transparent; color: #555; border: 1px solid #ccc; border-radius: 8px; padding: 10px 20px; font-size: 14px; text-decoration: none; display: inline-block; }
  .page-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 24px; }
  .page-header-left { display: flex; align-items: center; gap: 10px; }
  .icon-wrap { width: 32px; height: 32px; border-radius: 50%; background: #f5f5f5; border: 1px solid #ddd; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
  .lang-toggle { display: flex; gap: 4px; }
  .lang-btn { font-size: 12px; padding: 4px 10px; border-radius: 6px; border: 1px solid #ddd; background: #fff; color: #555; cursor: pointer; text-decoration: none; }
  .lang-btn.active { background: #111; color: #fff; border-color: #111; }
  .success-header { display: flex; align-items: center; gap: 10px; margin-bottom: 24px; }
  .icon-ok { width: 32px; height: 32px; border-radius: 50%; background: #d4edda; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
  label { font-size: 13px; color: #666; display: block; margin-bottom: 6px; }
  .field { margin-bottom: 20px; }
  input[type=text] { border: 1px solid #ccc; border-radius: 8px; padding: 8px 12px; font-size: 14px; outline: none; }
  input[type=text]:focus { border-color: #999; }
  input[type=number] { border: 1px solid #ccc; border-radius: 8px; padding: 8px 12px; font-size: 14px; outline: none; width: 100px; }
  input[type=number]:focus { border-color: #999; }
  .threshold-row { display: flex; align-items: center; gap: 10px; }
  .threshold-unit { font-size: 14px; color: #444; }
  .threshold-hint { font-size: 12px; color: #999; }
  .upload-area { border: 1px dashed #bbb; border-radius: 12px; padding: 24px; text-align: center; cursor: pointer; background: #fafafa; transition: background 0.15s; }
  .upload-area:hover { background: #f0f0f0; }
  .file-hint { font-size: 12px; color: #888; margin-top: 6px; }
  .captcha-row { display: flex; align-items: center; gap: 10px; }
  .captcha-q { font-size: 15px; font-weight: 500; background: #f5f5f5; border: 1px solid #ddd; border-radius: 8px; padding: 8px 14px; white-space: nowrap; }
  .captcha-row input { max-width: 80px; }
  .err { font-size: 13px; color: #c0392b; margin-top: 6px; display: none; }
  #file-name { margin-top: 8px; font-size: 13px; font-weight: 500; color: #185fa5; display: none; }
  table { border-collapse: collapse; width: 100%; margin-top: 10px; font-size: 0.9em; }
  th, td { border: 1px solid #ccc; padding: 6px 10px; text-align: left; }
  th { background: #f0f0f0; }
  tr:nth-child(even) { background: #fafafa; }
  .stat-kpi { display: inline-block; background:#f5f5f5; border:1px solid #ddd; border-radius:6px; padding:12px 24px; margin:6px; text-align:center; }
  .stat-kpi .val { font-size:2em; font-weight:bold; }
  .stat-kpi .lbl { font-size:0.8em; color:#666; }
  .donate-footer { margin-top: 48px; padding-top: 20px; border-top: 1px solid #eee; }
  .donate-banner { display: flex; align-items: center; justify-content: space-between; gap: 16px; background: #0057b8; border-radius: 10px; padding: 16px 20px; text-decoration: none; flex-wrap: wrap; }
  .donate-banner:hover { opacity: 0.93; }
  .donate-banner-left { display: flex; align-items: center; gap: 12px; }
  .donate-flag { font-size: 24px; flex-shrink: 0; }
  .donate-text { color: #fff; }
  .donate-text strong { display: block; font-size: 15px; font-weight: 500; }
  .donate-text span { font-size: 13px; opacity: 0.85; }
  .donate-btn { background: #ffd700; color: #0057b8; font-size: 13px; font-weight: 500; border-radius: 6px; padding: 8px 18px; white-space: nowrap; flex-shrink: 0; }
  .city-wrap { position: relative; display: inline-block; width: 280px; }
  .city-wrap input[type=text] { width: 100%; box-sizing: border-box; }
  .city-suggestions { position: absolute; top: 100%; left: 0; right: 0; background: #fff; border: 1px solid #ccc; border-radius: 8px; box-shadow: 0 4px 12px rgba(0,0,0,.1); z-index: 100; display: none; max-height: 220px; overflow-y: auto; margin-top: 2px; }
  .city-item { padding: 8px 12px; font-size: 14px; cursor: pointer; }
  .city-item:hover { background: #f5f5f5; }
  .field-hint { font-size: 12px; color: #999; margin-top: 5px; }
  #map { width: 100%; height: 420px; border-radius: 12px; border: 1px solid #ddd; margin-bottom: 8px; }
  .map-legend { display: flex; gap: 16px; margin-bottom: 24px; }
  .legend-item { display: flex; align-items: center; gap: 6px; font-size: 13px; color: #444; }
  .legend-dot { width: 12px; height: 12px; border-radius: 50%; flex-shrink: 0; }
  .legend-dot.ok { background: #2563eb; }
  .legend-dot.fixed { background: #e53e3e; }";
}

// ---------------------------------------------------------------------------
// Lang helpers
// ---------------------------------------------------------------------------
static class Lang
{
    public static string Detect(HttpRequest req)
    {
        var q = req.Query["lang"].ToString();
        if (q == "uk" || q == "en") return q;
        if (req.Cookies.TryGetValue("lang", out var c) && (c == "uk" || c == "en")) return c;
        var al = req.Headers["Accept-Language"].ToString();
        if (al.StartsWith("uk", StringComparison.OrdinalIgnoreCase)) return "uk";
        return "en";
    }

    public static void SetCookie(HttpResponse resp, string lang)
        => resp.Cookies.Append("lang", lang, new CookieOptions { MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax });

    public static string LangToggleHtml(string current, string path = "/")
        => $@"<div class='lang-toggle'>
  <a href='#' class='lang-btn{(current == "en" ? " active" : "")}' onclick='setLang(""en"")'>EN</a>
  <a href='#' class='lang-btn{(current == "uk" ? " active" : "")}' onclick='setLang(""uk"")'>UA</a>
</div>
<script>
function setLang(l){{
  document.cookie='lang='+l+';path=/;max-age=31536000;samesite=lax';
  var url=new URL(window.location.href);
  url.searchParams.set('lang',l);
  window.location.href=url.toString();
}}
</script>";

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["upload.title"]            = new() { ["en"] = "FIT Fixer",                                                       ["uk"] = "FIT Fixer" },
        ["upload.sub"]              = new() { ["en"] = "Fix GPS glitches in your activity file",                              ["uk"] = "Виправляє GPS-збої у файлі активності" },
        ["upload.tip"]              = new() { ["en"] = "Upload a <strong>.fit</strong> file recorded by your Garmin or other device. The service detects GPS points that would require exceeding the configured speed limit to reach, and replaces them with the last valid position.",
                                              ["uk"] = "Завантажте файл <strong>.fit</strong>, записаний вашим Garmin або іншим пристроєм. Сервіс виявляє GPS-точки, для досягнення яких потрібно перевищити задану швидкість, та замінює їх останньою коректною позицією." },
        ["upload.section"]          = new() { ["en"] = "Upload",                          ["uk"] = "Завантаження" },
        ["upload.file_label"]       = new() { ["en"] = "FIT file",                        ["uk"] = "FIT-файл" },
        ["upload.click"]            = new() { ["en"] = "Click to choose a file",          ["uk"] = "Натисніть, щоб вибрати файл" },
        ["upload.hint"]             = new() { ["en"] = "or drag and drop · .fit · max 5 MB", ["uk"] = "або перетягніть · .fit · макс. 5 МБ" },
        ["upload.start_label"]      = new() { ["en"] = "Start area",                           ["uk"] = "Регіон старту" },
        ["upload.start_ph"]         = new() { ["en"] = "City name…",                           ["uk"] = "Назва міста…" },
        ["upload.start_hint"]       = new() { ["en"] = "First GPS point further than 50 km from this city is treated as a pre-start glitch",
                                              ["uk"] = "Перша GPS-точка далі 50 км від цього міста вважається збоєм до старту" },
        ["upload.threshold_label"]  = new() { ["en"] = "Max speed",                            ["uk"] = "Максимальна швидкість" },
        ["upload.threshold_unit"]   = new() { ["en"] = "km/h",                                 ["uk"] = "км/год" },
        ["upload.threshold_hint"]   = new() { ["en"] = "Points implying faster travel than this are treated as GPS glitches (default: 70 km/h, range: 5–100)",
                                              ["uk"] = "Точки, що передбачають швидкість вище цієї, вважаються збоями GPS (за замовчуванням: 70 км/год, діапазон: 5–100)" },
        ["upload.captcha_label"]    = new() { ["en"] = "Human check — solve to continue", ["uk"] = "Перевірка — вирішіть приклад" },
        ["upload.captcha_eq"]       = new() { ["en"] = "=",                               ["uk"] = "=" },
        ["upload.captcha_ph"]       = new() { ["en"] = "?",                               ["uk"] = "?" },
        ["upload.captcha_err"]      = new() { ["en"] = "Incorrect answer — please try again", ["uk"] = "Неправильна відповідь — спробуйте ще раз" },
        ["upload.submit"]           = new() { ["en"] = "Upload &amp; Fix",                ["uk"] = "Завантажити та виправити" },
        ["upload.stats_link"]       = new() { ["en"] = "View stats →",                   ["uk"] = "Статистика →" },
        ["result.sub"]              = new() { ["en"] = "Completed in {0} ms",             ["uk"] = "Виконано за {0} мс" },
        ["result.coord_section"]    = new() { ["en"] = "Coordinate stats",                ["uk"] = "Статистика координат" },
        ["result.total"]            = new() { ["en"] = "Total record points",             ["uk"] = "Всього точок" },
        ["result.null"]             = new() { ["en"] = "Null coordinates",                ["uk"] = "Порожні координати" },
        ["result.map_section"]      = new() { ["en"] = "Track map",                           ["uk"] = "Карта треку" },
        ["result.map_ok"]           = new() { ["en"] = "Original track",                       ["uk"] = "Оригінальний трек" },
        ["result.map_fixed"]        = new() { ["en"] = "Fixed points",                         ["uk"] = "Виправлені точки" },
        ["result.map_start"]        = new() { ["en"] = "Start",                                ["uk"] = "Старт" },
        ["result.map_end"]          = new() { ["en"] = "Finish",                               ["uk"] = "Фініш" },
        ["result.jump"]             = new() { ["en"] = "Speed glitch >{0} km/h",               ["uk"] = "Збій швидкості >{0} км/год" },
        ["result.fixed"]            = new() { ["en"] = "Fixed points",                    ["uk"] = "Виправлено точок" },
        ["result.dropped"]          = new() { ["en"] = "Dropped messages",                ["uk"] = "Відкинуті повідомлення" },
        ["result.bad_ts"]           = new() { ["en"] = "{0} bad timestamp",               ["uk"] = "{0} некоректна мітка часу" },
        ["result.dup"]              = new() { ["en"] = "{0} duplicate file_id",           ["uk"] = "{0} дублікат file_id" },
        ["result.corrupt"]          = new() { ["en"] = "{0} corrupt",                     ["uk"] = "{0} пошкоджених" },
        ["result.tip"]              = new() { ["en"] = "If uploading the fixed file to Strava fails, try uploading it to <a href='https://www.fitfileviewer.com/' target='_blank'>fitfileviewer.com</a> — it can repair additional issues and help get your activity uploaded.",
                                              ["uk"] = "Якщо завантаження виправленого файлу до Strava не вдається, спробуйте <a href='https://www.fitfileviewer.com/' target='_blank'>fitfileviewer.com</a> — він може усунути додаткові проблеми." },
        ["result.download"]         = new() { ["en"] = "Download {0}",                    ["uk"] = "Завантажити {0}" },
        ["result.upload_another"]   = new() { ["en"] = "Upload another file",             ["uk"] = "Завантажити інший файл" },
        ["denied.title"]            = new() { ["en"] = "Access Denied",                   ["uk"] = "Доступ заборонено" },
        ["denied.heading"]          = new() { ["en"] = "Invalid API Key",                 ["uk"] = "Невірний ключ API" },
        ["denied.body"]             = new() { ["en"] = "The API key you entered is incorrect.", ["uk"] = "Введений ключ API є невірним." },
        ["denied.back"]             = new() { ["en"] = "← Return to upload page",         ["uk"] = "← Повернутися до завантаження" },
        ["stats.title"]             = new() { ["en"] = "FIT Fixer — Stats",               ["uk"] = "FIT Fixer — Статистика" },
        ["stats.heading"]           = new() { ["en"] = "Request Statistics",              ["uk"] = "Статистика запитів" },
        ["stats.back"]              = new() { ["en"] = "← Upload page",                   ["uk"] = "← Сторінка завантаження" },
        ["stats.summary"]           = new() { ["en"] = "Summary",                         ["uk"] = "Зведення" },
        ["stats.total"]             = new() { ["en"] = "Total requests",                  ["uk"] = "Всього запитів" },
        ["stats.succeeded"]         = new() { ["en"] = "Succeeded",                       ["uk"] = "Успішних" },
        ["stats.failed"]            = new() { ["en"] = "Failed",                          ["uk"] = "Помилок" },
        ["stats.avg_ms"]            = new() { ["en"] = "Avg processing time",             ["uk"] = "Сер. час обробки" },
        ["stats.sum_points"]        = new() { ["en"] = "Total points processed",          ["uk"] = "Всього точок оброблено" },
        ["stats.sum_fixed"]         = new() { ["en"] = "Total points fixed",              ["uk"] = "Всього точок виправлено" },
        ["stats.unique_ips"]        = new() { ["en"] = "Unique IPs",                      ["uk"] = "Унікальних IP" },
        ["stats.by_country"]        = new() { ["en"] = "Requests by country",             ["uk"] = "Запити за країнами" },
        ["stats.country"]           = new() { ["en"] = "Country",                         ["uk"] = "Країна" },
        ["stats.requests"]          = new() { ["en"] = "Requests",                        ["uk"] = "Запити" },
        ["stats.last50"]            = new() { ["en"] = "Last 50 requests",                ["uk"] = "Останні 50 запитів" },
        ["stats.col_time"]          = new() { ["en"] = "Time (UTC)",                      ["uk"] = "Час (UTC)" },
        ["stats.col_ip"]            = new() { ["en"] = "IP",                              ["uk"] = "IP" },
        ["stats.col_country"]       = new() { ["en"] = "Country",                         ["uk"] = "Країна" },
        ["stats.col_city"]          = new() { ["en"] = "City",                            ["uk"] = "Місто" },
        ["stats.col_file"]          = new() { ["en"] = "File",                            ["uk"] = "Файл" },
        ["stats.col_size"]          = new() { ["en"] = "Size",                            ["uk"] = "Розмір" },
        ["stats.col_points"]        = new() { ["en"] = "Points",                          ["uk"] = "Точки" },
        ["stats.col_fixed"]         = new() { ["en"] = "Fixed",                           ["uk"] = "Виправл." },
        ["stats.col_time2"]         = new() { ["en"] = "Time",                            ["uk"] = "Час" },
        ["stats.col_ok"]            = new() { ["en"] = "OK",                              ["uk"] = "OK" },
        ["stats.col_error"]         = new() { ["en"] = "Error",                           ["uk"] = "Помилка" },
    };

    public static string T(string key, string lang)
        => Strings.TryGetValue(key, out var d)
            ? (d.TryGetValue(lang, out var s) ? s : d["en"])
            : key;
}

