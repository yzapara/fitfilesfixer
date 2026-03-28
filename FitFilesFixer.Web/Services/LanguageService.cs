using Microsoft.AspNetCore.Http;

namespace FitFilesFixer.Web.Services;

public interface ILanguageService
{
    string Detect(HttpRequest request);
    void SetCookie(HttpResponse response, string lang);
    string LangToggleHtml(string current, string path = "/");
    string T(string key, string lang);
}

public class LanguageService : ILanguageService
{
    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["upload.title"]            = new() { ["en"] = "FIT Fixer", ["uk"] = "FIT Fixer" },
        ["upload.sub"]              = new() { ["en"] = "Fix GPS glitches in your activity file", ["uk"] = "Виправляє GPS-збої у файлі активності" },
        ["upload.tip"]              = new() { ["en"] = "Upload a <strong>.fit</strong> file recorded by your Garmin or other device. The service detects GPS points that would require exceeding the configured speed limit to reach, and replaces them with the last valid position.", ["uk"] = "Завантажте файл <strong>.fit</strong>, записаний вашим Garmin або іншим пристроєм. Сервіс виявляє GPS-точки, для досягнення яких потрібно перевищити задану швидкість, та замінює їх останньою коректною позицією." },
        ["upload.section"]          = new() { ["en"] = "Upload", ["uk"] = "Завантаження" },
        ["upload.file_label"]       = new() { ["en"] = "FIT file", ["uk"] = "FIT-файл" },
        ["upload.click"]            = new() { ["en"] = "Click to choose a file", ["uk"] = "Натисніть, щоб вибрати файл" },
        ["upload.hint"]             = new() { ["en"] = "or drag and drop · .fit · max 5 MB", ["uk"] = "або перетягніть · .fit · макс. 5 МБ" },
        ["upload.start_label"]      = new() { ["en"] = "Start area", ["uk"] = "Регіон старту" },
        ["upload.start_ph"]         = new() { ["en"] = "City name…", ["uk"] = "Назва міста…" },
        ["upload.start_hint"]       = new() { ["en"] = "First GPS point further than 50 km from this city is treated as a pre-start glitch", ["uk"] = "Перша GPS-точка далі 50 км від цього міста вважається збоєм до старту" },
        ["upload.threshold_label"]  = new() { ["en"] = "Max speed", ["uk"] = "Максимальна швидкість" },
        ["upload.threshold_unit"]   = new() { ["en"] = "km/h", ["uk"] = "км/год" },
        ["upload.threshold_hint"]   = new() { ["en"] = "Points implying faster travel than this are treated as GPS glitches (default: 70 km/h, range: 5–100)", ["uk"] = "Точки, що передбачають швидкість вище цієї, вважаються збоями GPS (за замовчуванням: 70 км/год, діапазон: 5–100)" },
        ["upload.captcha_label"]    = new() { ["en"] = "Human check — solve to continue", ["uk"] = "Перевірка — вирішіть приклад" },
        ["upload.captcha_eq"]       = new() { ["en"] = "=", ["uk"] = "=" },
        ["upload.captcha_ph"]       = new() { ["en"] = "?", ["uk"] = "?" },
        ["upload.captcha_err"]      = new() { ["en"] = "Incorrect answer — please try again", ["uk"] = "Неправильна відповідь — спробуйте ще раз" },
        ["upload.submit"]           = new() { ["en"] = "Upload &amp; Fix", ["uk"] = "Завантажити та виправити" },
        ["upload.stats_link"]       = new() { ["en"] = "View stats →", ["uk"] = "Статистика →" },
        ["result.sub"]              = new() { ["en"] = "Completed in {0} ms", ["uk"] = "Виконано за {0} мс" },
        ["result.coord_section"]    = new() { ["en"] = "Coordinate stats", ["uk"] = "Статистика координат" },
        ["result.total"]            = new() { ["en"] = "Total record points", ["uk"] = "Всього точок" },
        ["result.null"]             = new() { ["en"] = "Null coordinates", ["uk"] = "Порожні координати" },
        ["result.map_section"]      = new() { ["en"] = "Track map", ["uk"] = "Карта треку" },
        ["result.map_ok"]           = new() { ["en"] = "Original track", ["uk"] = "Оригінальний трек" },
        ["result.map_fixed"]        = new() { ["en"] = "Fixed points", ["uk"] = "Виправлені точки" },
        ["result.map_start"]        = new() { ["en"] = "Start", ["uk"] = "Старт" },
        ["result.map_end"]          = new() { ["en"] = "Finish", ["uk"] = "Фініш" },
        ["result.jump"]             = new() { ["en"] = "Speed glitch >{0} km/h", ["uk"] = "Збій швидкості >{0} км/год" },
        ["result.fixed"]            = new() { ["en"] = "Fixed points", ["uk"] = "Виправлено точок" },
        ["result.dropped"]          = new() { ["en"] = "Dropped messages", ["uk"] = "Відкинуті повідомлення" },
        ["result.bad_ts"]           = new() { ["en"] = "{0} bad timestamp", ["uk"] = "{0} некоректна мітка часу" },
        ["result.dup"]              = new() { ["en"] = "{0} duplicate file_id", ["uk"] = "{0} дублікат file_id" },
        ["result.corrupt"]          = new() { ["en"] = "{0} corrupt", ["uk"] = "{0} пошкоджених" },
        ["result.tip"]              = new() { ["en"] = "If uploading the fixed file to Strava fails, try uploading it to <a href='https://www.fitfileviewer.com/' target='_blank'>fitfileviewer.com</a> — it can repair additional issues and help get your activity uploaded.", ["uk"] = "Якщо завантаження виправленого файлу до Strava не вдається, спробуйте <a href='https://www.fitfileviewer.com/' target='_blank'>fitfileviewer.com</a> — він може усунути додаткові проблеми." },
        ["result.download"]         = new() { ["en"] = "Download {0}", ["uk"] = "Завантажити {0}" },
        ["result.download_original"] = new() { ["en"] = "Download original file", ["uk"] = "Завантажити оригінальний файл" },
        ["result.error_heading"]     = new() { ["en"] = "Processing failed", ["uk"] = "Обробка не вдалася" },
        ["result.error_body"]        = new() { ["en"] = "An error occurred during file processing.", ["uk"] = "Сталася помилка під час обробки файлу." },
        ["result.error_message"]     = new() { ["en"] = "Error details", ["uk"] = "Деталі помилки" },
        ["stats.col_download"]       = new() { ["en"] = "Download", ["uk"] = "Завантажити" },
        ["result.upload_another"]   = new() { ["en"] = "Upload another file", ["uk"] = "Завантажити інший файл" },
        ["denied.title"]            = new() { ["en"] = "Access Denied", ["uk"] = "Доступ заборонено" },
        ["denied.heading"]          = new() { ["en"] = "Invalid API Key", ["uk"] = "Невірний ключ API" },
        ["denied.body"]             = new() { ["en"] = "The API key you entered is incorrect.", ["uk"] = "Введений ключ API є невірним." },
        ["denied.back"]             = new() { ["en"] = "← Return to upload page", ["uk"] = "← Повернутися до завантаження" },
        ["stats.title"]             = new() { ["en"] = "FIT Fixer — Stats", ["uk"] = "FIT Fixer — Статистика" },
        ["stats.heading"]           = new() { ["en"] = "Request Statistics", ["uk"] = "Статистика запитів" },
        ["stats.back"]              = new() { ["en"] = "← Upload page", ["uk"] = "← Сторінка завантаження" },
        ["stats.summary"]           = new() { ["en"] = "Summary", ["uk"] = "Зведення" },
        ["stats.total"]             = new() { ["en"] = "Total requests", ["uk"] = "Всього запитів" },
        ["stats.succeeded"]         = new() { ["en"] = "Succeeded", ["uk"] = "Успішних" },
        ["stats.failed"]            = new() { ["en"] = "Failed", ["uk"] = "Помилок" },
        ["stats.avg_ms"]            = new() { ["en"] = "Avg processing time", ["uk"] = "Сер. час обробки" },
        ["stats.sum_points"]        = new() { ["en"] = "Total points processed", ["uk"] = "Всього точок оброблено" },
        ["stats.sum_fixed"]         = new() { ["en"] = "Total points fixed", ["uk"] = "Всього точок виправлено" },
        ["stats.unique_ips"]        = new() { ["en"] = "Unique IPs", ["uk"] = "Унікальних IP" },
        ["stats.by_city"]           = new() { ["en"] = "Requests by city", ["uk"] = "Запити за містами" },
        ["stats.by_country"]        = new() { ["en"] = "Requests by country", ["uk"] = "Запити за країнами" },
        ["stats.country"]           = new() { ["en"] = "Country", ["uk"] = "Країна" },
        ["stats.requests"]          = new() { ["en"] = "Requests", ["uk"] = "Запити" },
        ["stats.last50"]            = new() { ["en"] = "Last 50 requests", ["uk"] = "Останні 50 запитів" },
        ["stats.col_time"]          = new() { ["en"] = "Time (UTC)", ["uk"] = "Час (UTC)" },
        ["stats.col_ip"]            = new() { ["en"] = "IP", ["uk"] = "IP" },
        ["stats.col_country"]       = new() { ["en"] = "Country", ["uk"] = "Країна" },
        ["stats.city"]              = new() { ["en"] = "City", ["uk"] = "Місто" },
        ["stats.col_city"]          = new() { ["en"] = "City", ["uk"] = "Місто" },
        ["stats.col_file"]          = new() { ["en"] = "File", ["uk"] = "Файл" },
        ["stats.col_size"]          = new() { ["en"] = "Size", ["uk"] = "Розмір" },
        ["stats.col_points"]        = new() { ["en"] = "Points", ["uk"] = "Точки" },
        ["stats.col_fixed"]         = new() { ["en"] = "Fixed", ["uk"] = "Виправл." },
        ["stats.col_time2"]         = new() { ["en"] = "Time", ["uk"] = "Час" },
        ["stats.col_duration"]      = new() { ["en"] = "Duration", ["uk"] = "Тривалість" },
        ["stats.col_ok"]            = new() { ["en"] = "OK", ["uk"] = "OK" },
        ["stats.col_error"]         = new() { ["en"] = "Error", ["uk"] = "Помилка" },
    };

    public string Detect(HttpRequest req)
    {
        var q = req.Query["lang"].ToString();
        if (q == "uk" || q == "en")
            return q;

        if (req.Cookies.TryGetValue("lang", out var c) && (c == "uk" || c == "en"))
            return c;

        var al = req.Headers["Accept-Language"].ToString();
        if (al.StartsWith("uk", StringComparison.OrdinalIgnoreCase))
            return "uk";

        return "en";
    }

    public void SetCookie(HttpResponse resp, string lang)
        => resp.Cookies.Append("lang", lang, new CookieOptions { MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax });

    public string LangToggleHtml(string current, string path = "/")
    {
        var activeEn = current == "en" ? " active" : "";
        var activeUk = current == "uk" ? " active" : "";

        return $@"<div class='lang-toggle'>
  <a href='#' class='lang-btn{activeEn}' onclick='setLang(""en"")'>EN</a>
  <a href='#' class='lang-btn{activeUk}' onclick='setLang(""uk"")'>UA</a>
</div>
<script>
function setLang(l){{
  document.cookie='lang='+l+';path=/;max-age=31536000;samesite=lax';
  var url=new URL(window.location.href);
  url.searchParams.set('lang',l);
  window.location.href=url.toString();
}}
</script>";
    }

    public string T(string key, string lang)
        => Strings.TryGetValue(key, out var d) ? (d.TryGetValue(lang, out var s) ? s : d["en"]) : key;
}
