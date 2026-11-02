using Log4YM.Contracts.Api;
using Log4YM.Contracts.Models;

namespace Log4YM.Server.Services;

public interface IQsoService
{
    Task<QsoResponse?> GetByIdAsync(string id);
    Task<PaginatedQsoResponse> GetQsosAsync(QsoSearchRequest request);
    Task<QsoResponse> CreateAsync(CreateQsoRequest request);

    /// <summary>
    /// Save a fully-populated QSO (e.g. parsed from a WSJT-X logged-ADIF message), applying the
    /// active logbook and on-air callsign when the QSO doesn't carry its own, then broadcasting it
    /// like a QSO logged from the entry panel. Returns null if it duplicates one already logged.
    /// </summary>
    Task<QsoResponse?> CreateFromQsoAsync(Qso qso, bool skipDuplicates = true);

    Task<QsoResponse?> UpdateAsync(string id, UpdateQsoRequest request);
    Task<bool> DeleteAsync(string id);
    Task<QsoStatistics> GetStatisticsAsync();
}
