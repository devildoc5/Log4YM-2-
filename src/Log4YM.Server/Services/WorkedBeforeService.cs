using Log4YM.Contracts.Api;
using Log4YM.Contracts.Models;
using Log4YM.Server.Core.Database;

namespace Log4YM.Server.Services;

public interface IWorkedBeforeService
{
    /// <summary>
    /// Worked/confirmed status for a callsign, its grid square and its DXCC entity, overall and on
    /// the given band/mode. Pass a logbook to check only that log (e.g. a special event); null checks all.
    /// </summary>
    Task<WorkedBeforeResponse> GetAsync(string callsign, string? band, string? mode, string? grid, string? logbook);

    /// <summary>Mark the cache stale; the next lookup reloads the log. Called whenever QSOs change.</summary>
    void Invalidate();
}

/// <summary>
/// Answers "have I worked / confirmed this before?" from an in-memory index of the log, so the
/// entry panel can ask on every keystroke without hitting the database each time.
/// </summary>
public class WorkedBeforeService : IWorkedBeforeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkedBeforeService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private volatile IReadOnlyList<Entry>? _entries;

    private sealed record Entry(
        Qso Qso,
        string Call,
        string? Grid4,
        string? Entity,
        string Band,
        string Mode,
        string Logbook,
        bool Confirmed);

    public WorkedBeforeService(IServiceScopeFactory scopeFactory, ILogger<WorkedBeforeService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Invalidate() => _entries = null;

    public async Task<WorkedBeforeResponse> GetAsync(
        string callsign, string? band, string? mode, string? grid, string? logbook)
    {
        var entries = await GetEntriesAsync();

        var call = callsign.Trim().ToUpperInvariant();
        var normBand = string.IsNullOrWhiteSpace(band) ? null : band.Trim().ToLowerInvariant();
        var normMode = string.IsNullOrWhiteSpace(mode) ? null : NormalizeMode(mode);
        var grid4 = Grid4(grid);
        var entity = NormalizeEntity(CtyService.GetCountryFromCallsign(call).Country);

        var inLog = string.IsNullOrWhiteSpace(logbook)
            ? entries
            : entries.Where(e => string.Equals(e.Logbook, Logbooks.Normalize(logbook), StringComparison.OrdinalIgnoreCase)).ToList();

        var callMatches = inLog.Where(e => e.Call == call).ToList();

        // If the operator hasn't typed a grid yet, fall back to the one from the last QSO with them
        grid4 ??= callMatches.Select(e => e.Grid4).FirstOrDefault(g => g != null);

        var last = callMatches
            .OrderByDescending(e => e.Qso.QsoDate)
            .ThenByDescending(e => e.Qso.TimeOn)
            .FirstOrDefault();

        return new WorkedBeforeResponse(
            call,
            band,
            mode,
            logbook,
            Summarize(call, callMatches, normBand, normMode),
            grid4 is null ? null : Summarize(grid4, inLog.Where(e => e.Grid4 == grid4).ToList(), normBand, normMode),
            entity is null ? null : Summarize(entity, inLog.Where(e => e.Entity == entity).ToList(), normBand, normMode),
            last is null ? null : QsoService.MapToResponse(last.Qso));
    }

    private static WorkedStatus Summarize(string key, List<Entry> matches, string? band, string? mode)
    {
        bool OnBand(Entry e) => band != null && e.Band == band;
        bool OnMode(Entry e) => mode != null && e.Mode == mode;

        return new WorkedStatus(
            key,
            matches.Count,
            Worked: matches.Count > 0,
            WorkedOnBand: matches.Any(OnBand),
            WorkedOnMode: matches.Any(OnMode),
            WorkedOnBandMode: matches.Any(e => OnBand(e) && OnMode(e)),
            Confirmed: matches.Any(e => e.Confirmed),
            ConfirmedOnBand: matches.Any(e => e.Confirmed && OnBand(e)),
            ConfirmedOnMode: matches.Any(e => e.Confirmed && OnMode(e)),
            ConfirmedOnBandMode: matches.Any(e => e.Confirmed && OnBand(e) && OnMode(e)),
            BandsWorked: matches.Select(e => e.Band).Distinct().OrderBy(BandHelper.SortKey).ToList(),
            BandsConfirmed: matches.Where(e => e.Confirmed).Select(e => e.Band).Distinct().OrderBy(BandHelper.SortKey).ToList(),
            ModesWorked: matches.Select(e => e.Mode).Distinct().OrderBy(m => m).ToList());
    }

    private async Task<IReadOnlyList<Entry>> GetEntriesAsync()
    {
        var cached = _entries;
        if (cached != null) return cached;

        await _loadLock.WaitAsync();
        try
        {
            if (_entries != null) return _entries;

            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IQsoRepository>();
            var qsos = await repository.GetAllAsync();

            var list = qsos
                .Where(q => !string.IsNullOrWhiteSpace(q.Callsign))
                .Select(ToEntry)
                .ToList();

            _logger.LogDebug("Worked-before index built from {Count} QSOs", list.Count);
            _entries = list;
            return list;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static Entry ToEntry(Qso q)
    {
        var call = q.Callsign.Trim().ToUpperInvariant();
        var country = q.Country ?? q.Station?.Country;
        if (string.IsNullOrWhiteSpace(country))
            country = CtyService.GetCountryFromCallsign(call).Country;

        return new Entry(
            q,
            call,
            Grid4(q.Grid ?? q.Station?.Grid),
            NormalizeEntity(country),
            (q.Band ?? string.Empty).Trim().ToLowerInvariant(),
            NormalizeMode(q.Mode),
            Logbooks.Normalize(q.Logbook),
            IsConfirmed(q));
    }

    /// <summary>Confirmed by LoTW, by paper QSL, or by QRZ.</summary>
    internal static bool IsConfirmed(Qso q) =>
        string.Equals(q.Qsl?.Lotw?.Rcvd, "Y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(q.Qsl?.Rcvd, "Y", StringComparison.OrdinalIgnoreCase) ||
        q.QrzConfirmed;

    internal static string? Grid4(string? grid)
    {
        if (string.IsNullOrWhiteSpace(grid)) return null;
        var g = grid.Trim().ToUpperInvariant();
        return g.Length >= 4 ? g[..4] : null;
    }

    internal static string NormalizeMode(string? mode)
    {
        var m = (mode ?? string.Empty).Trim().ToUpperInvariant();
        return m is "USB" or "LSB" ? "SSB" : m;
    }

    private static string? NormalizeEntity(string? country) =>
        string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
}
