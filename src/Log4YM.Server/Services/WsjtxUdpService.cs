using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Log4YM.Contracts.Models;
using Log4YM.Server.Core.Database;

namespace Log4YM.Server.Services;

public record WsjtxStatus(
    bool Enabled,
    bool Listening,
    string Address,
    int Port,
    IReadOnlyList<string> ForwardTo,
    int QsosLogged,
    int DuplicatesSkipped,
    string? LastCallsign,
    DateTime? LastLoggedAt,
    string? LastClientId,
    string? LastError);

/// <summary>
/// Listens for the UDP messages WSJT-X, JTDX and MSHV send and saves each "Logged ADIF"
/// message (type 12, sent when you click Log QSO) as a QSO. Optionally re-sends every
/// datagram to other apps such as GridTracker, since only one program can own a unicast port.
/// </summary>
public class WsjtxUdpService : BackgroundService
{
    internal const uint Magic = 0xADBCCBDA;
    internal const uint TypeLoggedAdif = 12;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WsjtxUdpService> _logger;
    private readonly object _lock = new();

    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private WsjtxSettings _settings = new();
    private bool _listening;
    private int _qsosLogged;
    private int _duplicates;
    private string? _lastCallsign;
    private DateTime? _lastLoggedAt;
    private string? _lastClientId;
    private string? _lastError;

    public WsjtxUdpService(IServiceScopeFactory scopeFactory, ILogger<WsjtxUdpService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public WsjtxStatus GetStatus()
    {
        lock (_lock)
        {
            return new WsjtxStatus(_settings.Enabled, _listening, _settings.Address, _settings.Port,
                _settings.ForwardTo.ToList(), _qsosLogged, _duplicates, _lastCallsign, _lastLoggedAt,
                _lastClientId, _lastError);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReloadAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        await StopListenerAsync();
    }

    /// <summary>Re-read settings and restart the listener. Called on startup and when settings are saved.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        WsjtxSettings settings;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            settings = (await repo.GetAsync())?.Wsjtx ?? new WsjtxSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't load WSJT-X settings; listener stays off");
            settings = new WsjtxSettings();
        }

        await StopListenerAsync();

        lock (_lock)
        {
            _settings = settings;
            _lastError = null;
        }

        if (!settings.Enabled) return;

        var cts = new CancellationTokenSource();
        _listenerCts = cts;
        _listenerTask = Task.Run(() => ListenAsync(settings, cts.Token), CancellationToken.None);
    }

    private async Task StopListenerAsync()
    {
        var cts = _listenerCts;
        var task = _listenerTask;
        _listenerCts = null;
        _listenerTask = null;

        if (cts is null) return;
        cts.Cancel();
        try { if (task != null) await task; } catch { /* listener logs its own errors */ }
        cts.Dispose();
    }

    private async Task ListenAsync(WsjtxSettings settings, CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            var address = IPAddress.TryParse(settings.Address, out var a) ? a : IPAddress.Any;
            var isMulticast = IsMulticast(address);

            udp = new UdpClient(AddressFamily.InterNetwork);
            // Multicast lets WSJT-X, GridTracker and us all hear the same stream
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, isMulticast);
            udp.Client.Bind(new IPEndPoint(isMulticast ? IPAddress.Any : address, settings.Port));
            if (isMulticast)
            {
                udp.JoinMulticastGroup(address);
            }

            var forwardTargets = ParseTargets(settings.ForwardTo);

            lock (_lock) _listening = true;
            _logger.LogInformation("WSJT-X listener on {Address}:{Port}{Multicast}",
                address, settings.Port, isMulticast ? " (multicast)" : "");

            while (!ct.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(ct);

                foreach (var target in forwardTargets)
                {
                    try { await udp.SendAsync(received.Buffer, target, ct); }
                    catch (Exception ex) { _logger.LogDebug(ex, "WSJT-X forward to {Target} failed", target); }
                }

                await HandleDatagramAsync(received.Buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex)
        {
            var message = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"UDP port {settings.Port} is already in use — another program (GridTracker, JTAlert, another logger) is listening on it. Use a multicast address such as 224.0.0.73 in both WSJT-X and here, or have that program forward to Log4YM."
                : $"WSJT-X listener error: {ex.Message}";
            _logger.LogWarning(ex, "WSJT-X listener stopped");
            lock (_lock) _lastError = message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WSJT-X listener crashed");
            lock (_lock) _lastError = ex.Message;
        }
        finally
        {
            lock (_lock) _listening = false;
            udp?.Dispose();
        }
    }

    private async Task HandleDatagramAsync(byte[] buffer)
    {
        WsjtxMessage message;
        try
        {
            if (!WsjtxMessage.TryParse(buffer, out message)) return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring malformed WSJT-X datagram");
            return;
        }

        if (message.Type != TypeLoggedAdif || string.IsNullOrWhiteSpace(message.AdifText)) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var adif = scope.ServiceProvider.GetRequiredService<IAdifService>();
            var qsos = scope.ServiceProvider.GetRequiredService<IQsoService>();

            foreach (var qso in adif.ParseAdif(message.AdifText))
            {
                var saved = await qsos.CreateFromQsoAsync(qso, skipDuplicates: true);
                lock (_lock)
                {
                    _lastClientId = message.ClientId;
                    if (saved is null)
                    {
                        _duplicates++;
                    }
                    else
                    {
                        _qsosLogged++;
                        _lastCallsign = saved.Callsign;
                        _lastLoggedAt = DateTime.UtcNow;
                    }
                }

                _logger.LogInformation(saved is null
                    ? "WSJT-X QSO with {Call} already in log, skipped"
                    : "WSJT-X QSO with {Call} logged", qso.Callsign);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log WSJT-X QSO");
            lock (_lock) _lastError = $"Couldn't save a QSO from {message.ClientId}: {ex.Message}";
        }
    }

    internal static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return address.IsIPv6Multicast;
        var first = address.GetAddressBytes()[0];
        return first is >= 224 and <= 239;
    }

    internal static List<IPEndPoint> ParseTargets(IEnumerable<string> targets)
    {
        var list = new List<IPEndPoint>();
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var parts = t.Trim().Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[1], out var port) && port is > 0 and < 65536)
            {
                var host = parts[0].Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : parts[0];
                if (IPAddress.TryParse(host, out var ip))
                {
                    list.Add(new IPEndPoint(ip, port));
                }
            }
        }
        return list;
    }
}

/// <summary>
/// Minimal reader for the WSJT-X UDP message format (Qt QDataStream, big-endian):
/// quint32 magic, quint32 schema, quint32 type, utf8 id, then the type's fields.
/// Only the fields this app uses are decoded.
/// </summary>
internal readonly record struct WsjtxMessage(uint Schema, uint Type, string? ClientId, string? AdifText)
{
    public static bool TryParse(ReadOnlySpan<byte> data, out WsjtxMessage message)
    {
        message = default;
        var reader = new Reader(data);

        if (!reader.TryUInt32(out var magic) || magic != WsjtxUdpService.Magic) return false;
        if (!reader.TryUInt32(out var schema)) return false;
        if (!reader.TryUInt32(out var type)) return false;
        if (!reader.TryUtf8(out var id)) return false;

        string? adif = null;
        if (type == WsjtxUdpService.TypeLoggedAdif && !reader.TryUtf8(out adif)) return false;

        message = new WsjtxMessage(schema, type, id, adif);
        return true;
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

        public bool TryUInt32(out uint value)
        {
            value = 0;
            if (_pos + 4 > _data.Length) return false;
            value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return true;
        }

        // QByteArray-style utf8: quint32 length, 0xFFFFFFFF = null
        public bool TryUtf8(out string? value)
        {
            value = null;
            if (!TryUInt32(out var length)) return false;
            if (length == 0xFFFFFFFF) return true;
            if (_pos + length > _data.Length) return false;
            value = Encoding.UTF8.GetString(_data.Slice(_pos, (int)length));
            _pos += (int)length;
            return true;
        }
    }
}
