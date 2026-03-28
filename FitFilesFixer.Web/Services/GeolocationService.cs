using System.Net;
using System.Text.Json;

namespace FitFilesFixer.Web.Services;

public class GeolocationService : IGeolocationService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GeolocationService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public bool IsPrivateIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr))
            return true;

        var b = addr.GetAddressBytes();
        return addr.ToString() == "::1"
            || addr.IsIPv6LinkLocal
            || (b.Length == 4 && (
                b[0] == 10
             || b[0] == 127
             || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
             || (b[0] == 192 && b[1] == 168)));
    }

    public async Task<GeoLocationResult> GeolocateAsync(string ip)
    {
        const double defaultLat = 49.9935;
        const double defaultLon = 36.2304;
        const string defaultCountry = "Ukraine";
        const string defaultCity = "Kharkiv";

        if (string.IsNullOrEmpty(ip) || ip == "unknown" || IsPrivateIp(ip))
            return new GeoLocationResult(defaultCountry, defaultCity, defaultLat, defaultLon);

        try
        {
            var client = _httpClientFactory.CreateClient("geo");
            var resp = await client.GetAsync($"/json/{ip}?fields=status,country,city,lat,lon");
            if (!resp.IsSuccessStatusCode)
                return new GeoLocationResult(defaultCountry, defaultCity, defaultLat, defaultLon);

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() == "success")
            {
                var country = root.TryGetProperty("country", out var ctry) ? ctry.GetString() : null;
                var city = root.TryGetProperty("city", out var c) ? c.GetString() : null;
                var lat = root.TryGetProperty("lat", out var la) ? la.GetDouble() : defaultLat;
                var lon = root.TryGetProperty("lon", out var lo) ? lo.GetDouble() : defaultLon;
                if (!string.IsNullOrEmpty(city))
                    return new GeoLocationResult(country ?? defaultCountry, city, lat, lon);
            }
        }
        catch
        {
            // ignore
        }

        return new GeoLocationResult(defaultCountry, defaultCity, defaultLat, defaultLon);
    }
}
