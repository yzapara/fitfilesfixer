namespace FitFilesFixer.Web.Services;

public record GeoLocationResult(string Country, string City, double Lat, double Lon);

public interface IGeolocationService
{
    Task<GeoLocationResult> GeolocateAsync(string ip);
    bool IsPrivateIp(string ip);
}
