using System.Globalization;
using System.Text.Json;

namespace FitFilesFixer.Web.Services;

public class CityLookupService : ICityLookupService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public CityLookupService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IEnumerable<CitySearchResult>> SearchCitiesAsync(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Array.Empty<CitySearchResult>();

        try
        {
            var client = _httpClientFactory.CreateClient("nominatim");
            var url = $"/search?q={Uri.EscapeDataString(q)}&format=jsonv2&limit=8&featureType=city&addressdetails=1&accept-language=en";
            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<CitySearchResult>();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement.EnumerateArray()
                .Select(el =>
                {
                    var addr = el.TryGetProperty("address", out var a) ? a : default(JsonElement);
                    var city = GetAddressPart(addr, "city", "town", "village", "municipality");
                    var country = GetAddressPart(addr, "country");

                    if (IsBlockedCountry(country))
                        return null;

                    var display = !string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(country)
                        ? $"{city}, {country}"
                        : el.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? string.Empty : string.Empty;

                    if (string.IsNullOrWhiteSpace(display))
                        return null;

                    var lat = ParseCoord(el, "lat");
                    var lon = ParseCoord(el, "lon");
                    if (lat == null || lon == null)
                        return null;

                    return new CitySearchResult(display, lat.Value, lon.Value);
                })
                .Where(x => x != null)
                .Cast<CitySearchResult>()
                .ToList();

            return results;
        }
        catch
        {
            return Array.Empty<CitySearchResult>();
        }
    }

    private static bool IsBlockedCountry(string? country)
    {
        if (string.IsNullOrEmpty(country)) return false;
        var blocked = new[] { "Russia", "Belarus", "Iran", "North Korea" };
        return blocked.Any(b => country.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetAddressPart(JsonElement addr, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (addr.TryGetProperty(key, out var value))
                return value.GetString();
        }
        return null;
    }

    private static double? ParseCoord(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        if (double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;

        return null;
    }
}
