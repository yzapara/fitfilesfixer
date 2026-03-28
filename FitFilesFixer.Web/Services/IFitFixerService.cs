using FitFilesFixer.Web.Models;

namespace FitFilesFixer.Web.Services;

public interface IFitFixerService
{
    Task<FitProcessResult> ProcessAsync(Stream inputStream, string originalFileName, double maxSpeedKmh, double? startLat, double? startLon);
}
