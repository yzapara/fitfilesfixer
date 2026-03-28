using FitFilesFixer.Web.Models;

namespace FitFilesFixer.Web.Data;

public interface IRequestLogRepository
{
    void EnsureSchema(string connectionString);
    void Add(string connectionString, RequestLog log);
    Dictionary<string, object?> GetSummary(string connectionString);
    int GetUniqueIpCount(string connectionString);
    List<Dictionary<string, object?>> GetRequestsByCountry(string connectionString, int limit = 20);
    List<Dictionary<string, object?>> GetRequestsByCity(string connectionString, int limit = 20);
    List<Dictionary<string, object?>> GetLastRequests(string connectionString, int limit = 50);
}
