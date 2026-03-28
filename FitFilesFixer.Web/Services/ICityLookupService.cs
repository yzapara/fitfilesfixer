namespace FitFilesFixer.Web.Services;

public record CitySearchResult(string Name, double Latitude, double Longitude);

public interface ICityLookupService
{
    Task<IEnumerable<CitySearchResult>> SearchCitiesAsync(string q);
}
