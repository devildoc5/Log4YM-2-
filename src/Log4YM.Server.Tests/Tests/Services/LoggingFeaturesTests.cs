using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Log4YM.Contracts.Models;
using Log4YM.Server.Core.Database;
using Log4YM.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Log4YM.Server.Tests.Tests.Services;

[Trait("Category", "Unit")]
public class WsjtxMessageTests
{
    private static byte[] Utf8Field(string? s)
    {
        if (s is null) return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var bytes = Encoding.UTF8.GetBytes(s);
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
        return len.Concat(bytes).ToArray();
    }

    private static byte[] UInt32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Datagram(uint type, string id, params byte[][] fields) =>
        UInt32(WsjtxUdpService.Magic).Concat(UInt32(2)).Concat(UInt32(type)).Concat(Utf8Field(id))
            .Concat(fields.SelectMany(f => f)).ToArray();

    [Fact]
    public void LoggedAdif_IsDecoded()
    {
        const string adif = "<call:5>K7ABC <band:3>20m <mode:3>FT8 <qso_date:8>20261102 <time_on:6>143000 <eor>";
        var data = Datagram(WsjtxUdpService.TypeLoggedAdif, "WSJT-X", Utf8Field(adif));

        WsjtxMessage.TryParse(data, out var msg).Should().BeTrue();
        msg.Type.Should().Be(12u);
        msg.ClientId.Should().Be("WSJT-X");
        msg.AdifText.Should().Be(adif);
    }

    [Fact]
    public void Heartbeat_IsParsedWithoutAdif()
    {
        var data = Datagram(0, "JTDX", UInt32(3), Utf8Field("2.7.0"), Utf8Field(""));
        WsjtxMessage.TryParse(data, out var msg).Should().BeTrue();
        msg.Type.Should().Be(0u);
        msg.AdifText.Should().BeNull();
    }

    [Fact]
    public void WrongMagic_IsRejected()
    {
        var data = UInt32(0x12345678).Concat(UInt32(2)).Concat(UInt32(12)).ToArray();
        WsjtxMessage.TryParse(data, out _).Should().BeFalse();
    }

    [Fact]
    public void TruncatedAdif_IsRejected()
    {
        var full = Datagram(WsjtxUdpService.TypeLoggedAdif, "WSJT-X", Utf8Field("<call:5>K7ABC <eor>"));
        WsjtxMessage.TryParse(full.AsSpan(0, full.Length - 3), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("224.0.0.73", true)]
    [InlineData("239.255.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    public void Multicast_Detection(string address, bool expected) =>
        WsjtxUdpService.IsMulticast(System.Net.IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    public void ForwardTargets_IgnoreBadEntries()
    {
        var targets = WsjtxUdpService.ParseTargets(new[] { "127.0.0.1:2238", "localhost:2239", "nonsense", "1.2.3.4:99999", "" });
        targets.Select(t => t.ToString()).Should().Equal("127.0.0.1:2238", "127.0.0.1:2239");
    }
}

[Trait("Category", "Unit")]
public class ConfirmationMatchingTests
{
    private static Qso Q(string id, string call, string date, string time, string band, string mode, string? stationCall = null) => new()
    {
        Id = id,
        Callsign = call,
        QsoDate = DateTime.SpecifyKind(DateTime.Parse(date), DateTimeKind.Utc),
        TimeOn = time,
        Band = band,
        Mode = mode,
        StationCallsign = stationCall,
    };

    private static Dictionary<string, List<Qso>> Index(params Qso[] qsos) =>
        qsos.GroupBy(q => q.Callsign).ToDictionary(g => g.Key, g => g.ToList());

    [Fact]
    public void Matches_SameCallBandModeWithin30Minutes()
    {
        var local = Q("a", "K7ABC", "2026-11-02", "1430", "20m", "FT8");
        var remote = Q("r", "K7ABC", "2026-11-02", "145500", "20m", "FT8");
        ConfirmationSyncService.FindMatch(remote, Index(local), null)!.Id.Should().Be("a");
    }

    [Fact]
    public void NoMatch_OutsideWindow()
    {
        var local = Q("a", "K7ABC", "2026-11-02", "1430", "20m", "FT8");
        var remote = Q("r", "K7ABC", "2026-11-02", "1505", "20m", "FT8");
        ConfirmationSyncService.FindMatch(remote, Index(local), null).Should().BeNull();
    }

    [Fact]
    public void Matches_ModeGroup_SsbVsUsb_And_Ft4VsMfsk()
    {
        var ssb = Q("a", "K7ABC", "2026-11-02", "1430", "40m", "USB");
        ConfirmationSyncService.FindMatch(Q("r", "K7ABC", "2026-11-02", "1431", "40m", "SSB"), Index(ssb), null)!.Id.Should().Be("a");

        var ft4 = Q("b", "W1AW", "2026-11-02", "1430", "20m", "FT4");
        ConfirmationSyncService.FindMatch(Q("r", "W1AW", "2026-11-02", "1430", "20m", "MFSK"), Index(ft4), null)!.Id.Should().Be("b");
    }

    [Fact]
    public void NoMatch_DifferentBandOrModeGroup()
    {
        var local = Q("a", "K7ABC", "2026-11-02", "1430", "20m", "CW");
        ConfirmationSyncService.FindMatch(Q("r", "K7ABC", "2026-11-02", "1430", "40m", "CW"), Index(local), null).Should().BeNull();
        ConfirmationSyncService.FindMatch(Q("r", "K7ABC", "2026-11-02", "1430", "20m", "SSB"), Index(local), null).Should().BeNull();
    }

    [Fact]
    public void PrefersQsoLoggedUnderTheConfirmedStationCallsign()
    {
        var home = Q("home", "K7ABC", "2026-11-02", "1430", "20m", "SSB");
        var special = Q("event", "K7ABC", "2026-11-02", "1432", "20m", "SSB", stationCall: "W7A");
        var remote = Q("r", "K7ABC", "2026-11-02", "1430", "20m", "SSB", stationCall: "W7A");

        ConfirmationSyncService.FindMatch(remote, Index(home, special), "KF7XYZ")!.Id.Should().Be("event");
    }

    [Theory]
    [InlineData("1430", 14, 30, 0)]
    [InlineData("143015", 14, 30, 15)]
    [InlineData("14:30", 14, 30, 0)]
    public void QsoDateTime_CombinesDateAndTimeOn(string timeOn, int h, int m, int s)
    {
        var q = Q("a", "K7ABC", "2026-11-02", timeOn, "20m", "CW");
        ConfirmationSyncService.QsoDateTime(q).Should().Be(new DateTime(2026, 11, 2, h, m, s, DateTimeKind.Utc));
    }

    [Fact]
    public void QrzResponse_SplitsOnFirstEqualsAndUrlDecodes()
    {
        var parts = ConfirmationSyncService.ParseQrzResponse("RESULT=OK&COUNT=1&ADIF=%3Ccall%3A5%3EK7ABC%3Ceor%3E&LOGIDS=42");
        parts["RESULT"].Should().Be("OK");
        parts["ADIF"].Should().Be("<call:5>K7ABC<eor>");
        parts["LOGIDS"].Should().Be("42");
    }

    [Fact]
    public void ReadsLotwLastQslFromHeader()
    {
        const string adif = "ARRL Logbook of the World Status Report\n<APP_LoTW_LASTQSL:19>2026-10-30 18:22:01\n<eoh>\n<CALL:5>K7ABC<eor>";
        ConfirmationSyncService.ReadAdifHeaderField(adif, "APP_LoTW_LASTQSL").Should().Be("2026-10-30 18:22:01");
    }
}

[Trait("Category", "Unit")]
public class WorkedBeforeServiceTests
{
    private static Qso Q(string call, string band, string mode, string? grid = null, string? logbook = null, bool lotwConfirmed = false) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Callsign = call,
        QsoDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        TimeOn = "1200",
        Band = band,
        Mode = mode,
        Grid = grid,
        Country = "United States",
        Logbook = logbook,
        Qsl = lotwConfirmed ? new QslStatus { Lotw = new LotwStatus { Rcvd = "Y" } } : null,
    };

    private static WorkedBeforeService Service(params Qso[] qsos)
    {
        var repo = new Mock<IQsoRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(qsos);
        var services = new ServiceCollection().AddSingleton(repo.Object).BuildServiceProvider();
        return new WorkedBeforeService(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkedBeforeService>.Instance);
    }

    [Fact]
    public async Task NewCall_IsNotWorked()
    {
        var result = await Service().GetAsync("K7ABC", "20m", "FT8", null, null);
        result.Call.Worked.Should().BeFalse();
        result.LastQso.Should().BeNull();
    }

    [Fact]
    public async Task ReportsBandModeAndConfirmation()
    {
        var svc = Service(
            Q("K7ABC", "40m", "CW", "CN87", lotwConfirmed: true),
            Q("K7ABC", "20m", "FT8", "CN87"));

        var result = await svc.GetAsync("k7abc", "20m", "CW", null, null);

        result.Call.QsoCount.Should().Be(2);
        result.Call.WorkedOnBand.Should().BeTrue();        // 20m FT8
        result.Call.WorkedOnMode.Should().BeTrue();        // 40m CW
        result.Call.WorkedOnBandMode.Should().BeFalse();   // never 20m CW
        result.Call.Confirmed.Should().BeTrue();
        result.Call.ConfirmedOnBand.Should().BeFalse();
        result.Grid!.Key.Should().Be("CN87");              // taken from the earlier QSO
        result.Call.BandsWorked.Should().Equal("40m", "20m");
    }

    [Fact]
    public async Task UsbCountsAsSsb()
    {
        var result = await Service(Q("K7ABC", "40m", "USB")).GetAsync("K7ABC", "40m", "SSB", null, null);
        result.Call.WorkedOnBandMode.Should().BeTrue();
    }

    [Fact]
    public async Task LogbookFilter_OnlyCountsThatLogbook()
    {
        var svc = Service(Q("K7ABC", "20m", "SSB"), Q("K7ABC", "20m", "SSB", logbook: "W7A Event"));

        (await svc.GetAsync("K7ABC", "20m", "SSB", null, "W7A Event")).Call.QsoCount.Should().Be(1);
        (await svc.GetAsync("K7ABC", "20m", "SSB", null, "Main")).Call.QsoCount.Should().Be(1);
        (await svc.GetAsync("K7ABC", "20m", "SSB", null, null)).Call.QsoCount.Should().Be(2);
    }

    [Fact]
    public void Logbooks_NullIsMain()
    {
        Logbooks.Normalize(null).Should().Be("Main");
        Logbooks.Matches(new Qso { Callsign = "X", Logbook = null }, "main").Should().BeTrue();
        Logbooks.Matches(new Qso { Callsign = "X", Logbook = "Event" }, "Main").Should().BeFalse();
        Logbooks.Matches(new Qso { Callsign = "X", Logbook = "Event" }, null).Should().BeTrue();
    }
}
