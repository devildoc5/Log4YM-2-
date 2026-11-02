using Microsoft.AspNetCore.SignalR;
using Log4YM.Contracts.Api;
using Log4YM.Contracts.Models;
using Log4YM.Contracts.Events;
using Log4YM.Server.Core.Database;
using Log4YM.Server.Hubs;

namespace Log4YM.Server.Services;

public class QsoService : IQsoService
{
    private readonly IQsoRepository _repository;
    private readonly IHubContext<LogHub, ILogHubClient> _hub;
    private readonly ISpotStatusService? _spotStatusService;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly IWorkedBeforeService? _workedBefore;

    public QsoService(
        IQsoRepository repository,
        IHubContext<LogHub, ILogHubClient> hub,
        ISpotStatusService? spotStatusService = null,
        ISettingsRepository? settingsRepository = null,
        IWorkedBeforeService? workedBefore = null)
    {
        _repository = repository;
        _hub = hub;
        _spotStatusService = spotStatusService;
        _settingsRepository = settingsRepository;
        _workedBefore = workedBefore;
    }

    public async Task<QsoResponse?> GetByIdAsync(string id)
    {
        var qso = await _repository.GetByIdAsync(id);
        return qso is null ? null : MapToResponse(qso);
    }

    public async Task<PaginatedQsoResponse> GetQsosAsync(QsoSearchRequest request)
    {
        var (items, totalCount) = await _repository.SearchAsync(request);
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.Limit);
        var page = (request.Skip / request.Limit) + 1;

        return new PaginatedQsoResponse(
            Items: items.Select(MapToResponse),
            TotalCount: totalCount,
            Page: page,
            PageSize: request.Limit,
            TotalPages: totalPages
        );
    }

    public async Task<QsoResponse> CreateAsync(CreateQsoRequest request)
    {
        var qso = new Qso
        {
            Callsign = request.Callsign.ToUpperInvariant(),
            QsoDate = request.QsoDate,
            TimeOn = request.TimeOn,
            Band = request.Band,
            Mode = request.Mode,
            Frequency = request.Frequency,
            RstSent = request.RstSent,
            RstRcvd = request.RstRcvd,
            Comment = request.Comment,
            Logbook = request.Logbook,
            StationCallsign = request.StationCallsign,
            Station = new StationInfo
            {
                Name = request.Name,
                Grid = request.Grid,
                Country = request.Country
            }
        };

        // Manual entry: never silently drop a QSO the operator typed in
        return (await SaveAsync(qso, skipDuplicates: false))!;
    }

    public Task<QsoResponse?> CreateFromQsoAsync(Qso qso, bool skipDuplicates = true)
    {
        qso.Callsign = qso.Callsign.ToUpperInvariant();
        return SaveAsync(qso, skipDuplicates);
    }

    private async Task<QsoResponse?> SaveAsync(Qso qso, bool skipDuplicates)
    {
        await ApplyLogbookAsync(qso);

        if (skipDuplicates &&
            await _repository.ExistsAsync(qso.Callsign, qso.QsoDate, qso.TimeOn, qso.Band, qso.Mode))
        {
            return null;
        }

        var created = await _repository.CreateAsync(qso);

        // Update spot status cache incrementally
        _spotStatusService?.OnQsoLogged(
            created.Callsign,
            created.Country ?? created.Station?.Country,
            created.Band,
            created.Mode);
        _workedBefore?.Invalidate();

        // Broadcast to all clients via SignalR
        await _hub.BroadcastQso(new QsoLoggedEvent(
            created.Id,
            created.Callsign,
            created.QsoDate,
            created.TimeOn,
            created.Band,
            created.Mode,
            created.Frequency,
            created.RstSent,
            created.RstRcvd,
            created.Grid ?? created.Station?.Grid
        ));

        return MapToResponse(created);
    }

    /// <summary>
    /// Fill in the logbook (the active one from Settings) and the on-air callsign (from that
    /// logbook's definition) when the QSO doesn't already carry them.
    /// </summary>
    private async Task ApplyLogbookAsync(Qso qso)
    {
        if (_settingsRepository is null)
        {
            qso.Logbook = Logbooks.Normalize(qso.Logbook);
            return;
        }

        var settings = await _settingsRepository.GetAsync() ?? new UserSettings();
        var logbookSettings = settings.Logbooks ?? new LogbookSettings();

        qso.Logbook = Logbooks.Normalize(
            string.IsNullOrWhiteSpace(qso.Logbook) ? logbookSettings.Active : qso.Logbook);

        if (string.IsNullOrWhiteSpace(qso.StationCallsign))
        {
            var definition = logbookSettings.Logbooks.FirstOrDefault(l =>
                string.Equals(Logbooks.Normalize(l.Name), qso.Logbook, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(definition?.StationCallsign))
            {
                qso.StationCallsign = definition.StationCallsign.Trim().ToUpperInvariant();
            }
        }
        else
        {
            qso.StationCallsign = qso.StationCallsign.Trim().ToUpperInvariant();
        }
    }

    public async Task<QsoResponse?> UpdateAsync(string id, UpdateQsoRequest request)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing is null) return null;

        if (request.Callsign != null) existing.Callsign = request.Callsign.ToUpperInvariant();
        if (request.QsoDate.HasValue) existing.QsoDate = request.QsoDate.Value;
        if (request.TimeOn != null) existing.TimeOn = request.TimeOn;
        if (request.Band != null) existing.Band = request.Band;
        if (request.Mode != null) existing.Mode = request.Mode;
        if (request.Frequency.HasValue) existing.Frequency = request.Frequency;
        if (request.RstSent != null) existing.RstSent = request.RstSent;
        if (request.RstRcvd != null) existing.RstRcvd = request.RstRcvd;
        if (request.Comment != null) existing.Comment = request.Comment;
        if (request.Logbook != null) existing.Logbook = Logbooks.Normalize(request.Logbook);
        if (request.StationCallsign != null)
            existing.StationCallsign = string.IsNullOrWhiteSpace(request.StationCallsign)
                ? null
                : request.StationCallsign.Trim().ToUpperInvariant();

        existing.Station ??= new StationInfo();
        if (request.Name != null) existing.Station.Name = request.Name;
        if (request.Grid != null) existing.Station.Grid = request.Grid;
        if (request.Country != null) existing.Station.Country = request.Country;

        await _repository.UpdateAsync(id, existing);
        _workedBefore?.Invalidate();
        return MapToResponse(existing);
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var deleted = await _repository.DeleteAsync(id);
        if (deleted) _workedBefore?.Invalidate();
        return deleted;
    }

    public async Task<QsoStatistics> GetStatisticsAsync()
    {
        return await _repository.GetStatisticsAsync();
    }

    internal static QsoResponse MapToResponse(Qso qso) => new(
        qso.Id,
        qso.Callsign,
        qso.QsoDate,
        qso.TimeOn,
        qso.TimeOff,
        qso.Band,
        qso.Mode,
        qso.Frequency,
        qso.RstSent,
        qso.RstRcvd,
        new StationInfoDto(
            qso.Name ?? qso.Station?.Name,
            qso.Grid ?? qso.Station?.Grid,
            qso.Country ?? qso.Station?.Country,
            qso.Dxcc ?? qso.Station?.Dxcc,
            qso.Station?.State,
            qso.Continent ?? qso.Station?.Continent,
            qso.Station?.Latitude,
            qso.Station?.Longitude
        ),
        qso.Comment,
        qso.CreatedAt,
        Logbooks.Normalize(qso.Logbook),
        qso.StationCallsign,
        qso.Qsl?.Lotw?.Sent,
        qso.Qsl?.Lotw?.Rcvd,
        qso.QrzSyncStatus == SyncStatus.Synced,
        qso.QrzConfirmed
    );
}
