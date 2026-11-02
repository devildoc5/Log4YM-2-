using System.Net;
using System.Text.RegularExpressions;
using Log4YM.Contracts.Api;
using Log4YM.Contracts.Models;
using Log4YM.Server.Core.Database;

namespace Log4YM.Server.Services;

public interface IConfirmationSyncService
{
    /// <summary>
    /// Download QSL confirmations from LoTW (lotwreport.adi) and mark matching QSOs LoTW-confirmed.
    /// Incremental by default: asks only for QSLs since the last download. <paramref name="full"/> re-reads everything.
    /// </summary>
    Task<ConfirmationDownloadResult> DownloadLotwAsync(bool full, CancellationToken cancellationToken);

    /// <summary>Download confirmed QSOs from the QRZ logbook (FETCH STATUS:CONFIRMED) and mark matches QRZ-confirmed.</summary>
    Task<ConfirmationDownloadResult> DownloadQrzAsync(CancellationToken cancellationToken);
}

public class ConfirmationSyncService : IConfirmationSyncService
{
    private const string LotwReportUrl = "https://lotw.arrl.org/lotwuser/lotwreport.adi";
    private const string QrzLogbookApiUrl = "https://logbook.qrz.com/api";
    private const int QrzPageSize = 250;

    // LoTW (and QRZ) consider a QSO a match when the times are within 30 minutes
    private static readonly TimeSpan MatchWindow = TimeSpan.FromMinutes(30);

    private readonly ISettingsRepository _settingsRepository;
    private readonly IQsoRepository _qsoRepository;
    private readonly IAdifService _adifService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWorkedBeforeService? _workedBefore;
    private readonly ILogger<ConfirmationSyncService> _logger;

    public ConfirmationSyncService(
        ISettingsRepository settingsRepository,
        IQsoRepository qsoRepository,
        IAdifService adifService,
        IHttpClientFactory httpClientFactory,
        ILogger<ConfirmationSyncService> logger,
        IWorkedBeforeService? workedBefore = null)
    {
        _settingsRepository = settingsRepository;
        _qsoRepository = qsoRepository;
        _adifService = adifService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _workedBefore = workedBefore;
    }

    // ---------------------------------------------------------------- LoTW

    public async Task<ConfirmationDownloadResult> DownloadLotwAsync(bool full, CancellationToken cancellationToken)
    {
        var settings = await _settingsRepository.GetAsync() ?? new UserSettings();
        var lotw = settings.Lotw;

        if (string.IsNullOrWhiteSpace(lotw.Username) || string.IsNullOrWhiteSpace(lotw.Password))
        {
            return Fail("LoTW username and password aren't set. Add them in Settings → LOTW (they're your lotw.arrl.org website login, not your TQSL certificate).");
        }

        var since = full || string.IsNullOrWhiteSpace(lotw.LastQslDownload) ? "1900-01-01" : lotw.LastQslDownload!;

        var query = new Dictionary<string, string>
        {
            ["login"] = lotw.Username!,
            ["password"] = lotw.Password!,
            ["qso_query"] = "1",
            ["qso_qsl"] = "yes",
            ["qso_qslsince"] = since,
            ["qso_qsldetail"] = "yes",
            ["qso_withown"] = "yes",
        };
        var url = LotwReportUrl + "?" + string.Join("&",
            query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        string body;
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5); // a full download of a big log is slow
            body = await http.GetStringAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "LoTW download failed");
            return Fail($"Couldn't reach LoTW: {ex.Message}");
        }

        // A failed query (bad login, LoTW down) returns an HTML page instead of ADIF
        if (body.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return Fail("LoTW didn't return a logbook report. Check your LoTW username and password in Settings → LOTW.");
        }

        var remote = _adifService.ParseAdif(body).ToList();
        var result = await ApplyAsync(remote, settings, MarkLotwConfirmed, cancellationToken);

        var lastQsl = ReadAdifHeaderField(body, "APP_LoTW_LASTQSL");
        if (!string.IsNullOrWhiteSpace(lastQsl))
        {
            settings.Lotw.LastQslDownload = lastQsl;
            await _settingsRepository.UpsertAsync(settings);
        }

        return result with
        {
            Message = remote.Count == 0
                ? "No new LoTW confirmations since the last download."
                : $"Downloaded {remote.Count} LoTW confirmation(s): {result.NewlyConfirmed} newly confirmed, {result.Matched - result.NewlyConfirmed} already marked, {result.Unmatched} not found in your log."
        };
    }

    private static bool MarkLotwConfirmed(Qso local, Qso remote)
    {
        local.Qsl ??= new QslStatus();
        local.Qsl.Lotw ??= new LotwStatus();

        var already = string.Equals(local.Qsl.Lotw.Rcvd, "Y", StringComparison.OrdinalIgnoreCase);
        local.Qsl.Lotw.Rcvd = "Y";
        local.Qsl.Lotw.RcvdDate = remote.Qsl?.RcvdDate ?? remote.Qsl?.Lotw?.RcvdDate ?? local.Qsl.Lotw.RcvdDate ?? DateTime.UtcNow;

        // It's confirmed on LoTW, so it was evidently uploaded
        if (string.IsNullOrWhiteSpace(local.Qsl.Lotw.Sent))
        {
            local.Qsl.Lotw.Sent = "Y";
            local.LotwSyncStatus = SyncStatus.Synced;
            local.LotwSyncedAt ??= DateTime.UtcNow;
        }

        FillMissingDetails(local, remote);
        return !already;
    }

    // ---------------------------------------------------------------- QRZ

    public async Task<ConfirmationDownloadResult> DownloadQrzAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsRepository.GetAsync() ?? new UserSettings();
        var apiKey = settings.Qrz.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Fail("QRZ logbook API key isn't set. Add it in Settings → QRZ.");
        }

        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);

        var remote = new List<Qso>();
        long afterLogId = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("KEY", apiKey),
                new KeyValuePair<string, string>("ACTION", "FETCH"),
                new KeyValuePair<string, string>("OPTION", $"STATUS:CONFIRMED,MAX:{QrzPageSize},AFTERLOGID:{afterLogId}"),
            });

            string text;
            try
            {
                using var response = await http.PostAsync(QrzLogbookApiUrl, content, cancellationToken);
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "QRZ fetch failed");
                return Fail($"Couldn't reach QRZ: {ex.Message}");
            }

            var parts = ParseQrzResponse(text);
            parts.TryGetValue("RESULT", out var resultCode);
            parts.TryGetValue("COUNT", out var countText);
            parts.TryGetValue("ADIF", out var adif);

            if (!string.Equals(resultCode, "OK", StringComparison.OrdinalIgnoreCase))
            {
                // QRZ answers FAIL with COUNT=0 when nothing matches — that's not an error
                if (countText == "0" || (remote.Count > 0 && string.IsNullOrEmpty(adif)))
                    break;

                parts.TryGetValue("REASON", out var reason);
                return Fail($"QRZ refused the download: {reason ?? text}. Fetching needs an XML Logbook Data subscription and the API key for this logbook.");
            }

            var page = string.IsNullOrWhiteSpace(adif) ? new List<Qso>() : _adifService.ParseAdif(adif).ToList();
            remote.AddRange(page);

            if (page.Count < QrzPageSize) break;

            var highest = page
                .Select(q => long.TryParse(GetExtra(q, "app_qrzlog_logid"), out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();
            if (highest <= afterLogId) break; // no logids to page on — stop rather than loop forever
            afterLogId = highest + 1;
        }

        var result = await ApplyAsync(remote, settings, MarkQrzConfirmed, cancellationToken);
        return result with
        {
            Message = remote.Count == 0
                ? "QRZ has no confirmed QSOs in this logbook yet."
                : $"Downloaded {remote.Count} QRZ confirmation(s): {result.NewlyConfirmed} newly confirmed, {result.Matched - result.NewlyConfirmed} already marked, {result.Unmatched} not found in your log."
        };
    }

    private static bool MarkQrzConfirmed(Qso local, Qso remote)
    {
        var already = local.QrzConfirmed;
        local.QrzConfirmed = true;
        local.QrzConfirmedAt ??= DateTime.UtcNow;

        var logId = GetExtra(remote, "app_qrzlog_logid");
        if (string.IsNullOrWhiteSpace(local.QrzLogId) && !string.IsNullOrWhiteSpace(logId))
        {
            local.QrzLogId = logId;
        }

        FillMissingDetails(local, remote);
        return !already;
    }

    internal static Dictionary<string, string> ParseQrzResponse(string text)
    {
        // RESULT=OK&COUNT=2&ADIF=<url-encoded adif>&LOGIDS=1,2 — split on the first '=' only
        return text.Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => WebUtility.UrlDecode(g.First()[1]), StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- matching

    private async Task<ConfirmationDownloadResult> ApplyAsync(
        List<Qso> remote,
        UserSettings settings,
        Func<Qso, Qso, bool> mark,
        CancellationToken cancellationToken)
    {
        if (remote.Count == 0)
        {
            return new ConfirmationDownloadResult(true, string.Empty, 0, 0, 0, 0, Array.Empty<string>());
        }

        var local = (await _qsoRepository.GetAllAsync())
            .Where(q => !string.IsNullOrWhiteSpace(q.Callsign))
            .GroupBy(q => q.Callsign.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var defaultCall = settings.Station?.Callsign?.Trim().ToUpperInvariant();
        var changed = new Dictionary<string, Qso>();
        var matched = 0;
        var newlyConfirmed = 0;
        var unmatched = new List<string>();

        foreach (var r in remote)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = FindMatch(r, local, defaultCall);
            if (match is null)
            {
                unmatched.Add($"{r.Callsign} {r.QsoDate:yyyy-MM-dd} {r.Band} {r.Mode}");
                continue;
            }

            matched++;
            if (mark(match, r))
            {
                newlyConfirmed++;
                changed[match.Id] = match;
            }
        }

        foreach (var qso in changed.Values)
        {
            var wasSyncedToQrz = qso.QrzSyncStatus == SyncStatus.Synced && !string.IsNullOrEmpty(qso.QrzLogId);
            await _qsoRepository.UpdateAsync(qso.Id, qso);

            // UpdateAsync flags synced QSOs as "modified" so they re-upload to QRZ. A confirmation
            // arriving isn't an edit the operator made, so put the sync status back.
            if (wasSyncedToQrz)
            {
                await _qsoRepository.UpdateQrzSyncStatusAsync(qso.Id, qso.QrzLogId!);
            }
        }

        if (changed.Count > 0) _workedBefore?.Invalidate();

        return new ConfirmationDownloadResult(
            true, string.Empty, remote.Count, matched, newlyConfirmed, unmatched.Count, unmatched.Take(25).ToList());
    }

    internal static Qso? FindMatch(Qso remote, Dictionary<string, List<Qso>> localByCall, string? defaultStationCall)
    {
        var call = remote.Callsign?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(call) || !localByCall.TryGetValue(call, out var candidates)) return null;

        var remoteTime = QsoDateTime(remote);
        var remoteBand = remote.Band?.Trim().ToLowerInvariant();
        var remoteMode = ModeGroup(GetExtra(remote, "submode") ?? remote.Mode);
        var remoteStation = (remote.StationCallsign ?? GetExtra(remote, "app_lotw_owncall"))?.Trim().ToUpperInvariant();

        var hits = candidates
            .Where(q => string.Equals(q.Band?.Trim(), remoteBand, StringComparison.OrdinalIgnoreCase))
            .Where(q => ModeGroup(q.Mode) == remoteMode || ModeGroup(GetExtra(q, "submode") ?? q.Mode) == remoteMode)
            .Select(q => (Qso: q, Delta: (QsoDateTime(q) - remoteTime).Duration()))
            .Where(x => x.Delta <= MatchWindow)
            .ToList();

        if (hits.Count == 0) return null;

        // Same contact worked from two of your callsigns (e.g. home call and a special-event call):
        // prefer the QSO logged under the callsign the confirmation is for.
        if (!string.IsNullOrEmpty(remoteStation) && hits.Count > 1)
        {
            var sameStation = hits.Where(x =>
                string.Equals(x.Qso.StationCallsign ?? defaultStationCall, remoteStation, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameStation.Count > 0) hits = sameStation;
        }

        return hits.OrderBy(x => x.Delta).First().Qso;
    }

    /// <summary>QSO start time in UTC, built from the date plus TIME_ON when the date has no time part.</summary>
    internal static DateTime QsoDateTime(Qso q)
    {
        var date = q.QsoDate;
        if (date.TimeOfDay != TimeSpan.Zero) return date;

        var digits = new string((q.TimeOn ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length >= 4 &&
            int.TryParse(digits[..2], out var h) &&
            int.TryParse(digits[2..4], out var m))
        {
            var s = digits.Length >= 6 && int.TryParse(digits[4..6], out var sec) ? sec : 0;
            return date.Date.AddHours(h).AddMinutes(m).AddSeconds(s);
        }

        return date;
    }

    /// <summary>LoTW matches on mode group (CW / phone / data), not the exact mode.</summary>
    internal static string ModeGroup(string? mode)
    {
        var m = (mode ?? string.Empty).Trim().ToUpperInvariant();
        return m switch
        {
            "CW" => "CW",
            "SSB" or "USB" or "LSB" or "AM" or "FM" or "PHONE" or "DIGITALVOICE" or "C4FM" or "DSTAR" or "DMR" => "PHONE",
            "" => "",
            _ => "DATA"
        };
    }

    /// <summary>Confirmations carry the other station's details; use them where our record is blank.</summary>
    private static void FillMissingDetails(Qso local, Qso remote)
    {
        var grid = remote.Grid ?? remote.Station?.Grid;
        if (string.IsNullOrWhiteSpace(local.Grid ?? local.Station?.Grid) && !string.IsNullOrWhiteSpace(grid))
        {
            local.Grid = grid;
            local.Station ??= new StationInfo();
            local.Station.Grid = grid;
        }

        if (local.Dxcc is null && remote.Dxcc is not null) local.Dxcc = remote.Dxcc;
        if (string.IsNullOrWhiteSpace(local.Country) && !string.IsNullOrWhiteSpace(remote.Country)) local.Country = remote.Country;
    }

    private static string? GetExtra(Qso q, string name)
    {
        if (q.AdifExtra is null) return null;
        foreach (var element in q.AdifExtra)
        {
            if (element.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var v = element.Value?.ToString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        return null;
    }

    internal static string? ReadAdifHeaderField(string adif, string field)
    {
        var header = adif[..Math.Max(0, adif.IndexOf("<eoh>", StringComparison.OrdinalIgnoreCase))];
        var match = Regex.Match(header, $@"<{Regex.Escape(field)}:(\d+)(?::[^>]*)?>", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var length = int.Parse(match.Groups[1].Value);
        var start = match.Index + match.Length;
        return start + length <= header.Length ? header.Substring(start, length).Trim() : null;
    }

    private static ConfirmationDownloadResult Fail(string message) =>
        new(false, message, 0, 0, 0, 0, Array.Empty<string>());
}
