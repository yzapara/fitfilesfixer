using FitFilesFixer.Web.Models;

namespace FitFilesFixer.Web.Services;

public interface IRequestLogService
{
    Task LogAsync(string connectionString, RequestLog log);
}
