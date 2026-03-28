using FitFilesFixer.Web.DataAccess;
using FitFilesFixer.Web.Models;

namespace FitFilesFixer.Web.Services;

public class RequestLogService : IRequestLogService
{
    private readonly IRequestLogRepository _repository;
    private readonly IGeolocationService _geo;

    public RequestLogService(IRequestLogRepository repository, IGeolocationService geo)
    {
        _repository = repository;
        _geo = geo;
    }

    public async Task LogAsync(string connectionString, RequestLog log)
    {
        if (!string.IsNullOrEmpty(log.Ip) && log.Ip != "unknown" && !_geo.IsPrivateIp(log.Ip))
        {
            var geo = await _geo.GeolocateAsync(log.Ip);
            log = log with { Country = geo.Country, City = geo.City };
        }

        _repository.Add(connectionString, log);
    }
}
