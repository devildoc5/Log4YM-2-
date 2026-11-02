namespace Log4YM.Contracts.Api;

public record CreateQsoRequest(
    string Callsign,
    DateTime QsoDate,
    string TimeOn,
    string Band,
    string Mode,
    double? Frequency = null,
    string? RstSent = null,
    string? RstRcvd = null,
    string? Name = null,
    string? Grid = null,
    string? Country = null,
    string? Comment = null,
    // Logbook to log into; null = the active logbook from Settings
    string? Logbook = null,
    // On-air callsign; null = the logbook's callsign, else the station callsign
    string? StationCallsign = null
);

public record UpdateQsoRequest(
    string? Callsign = null,
    DateTime? QsoDate = null,
    string? TimeOn = null,
    string? Band = null,
    string? Mode = null,
    double? Frequency = null,
    string? RstSent = null,
    string? RstRcvd = null,
    string? Name = null,
    string? Grid = null,
    string? Country = null,
    string? Comment = null,
    string? Logbook = null,
    string? StationCallsign = null
);

public record QsoResponse(
    string Id,
    string Callsign,
    DateTime QsoDate,
    string TimeOn,
    string? TimeOff,
    string Band,
    string Mode,
    double? Frequency,
    string? RstSent,
    string? RstRcvd,
    StationInfoDto? Station,
    string? Comment,
    DateTime CreatedAt,
    string? Logbook = null,
    string? StationCallsign = null,
    // Confirmation state: LoTW sent/received ('Y'/'N'/null) and QRZ confirmed
    string? LotwSent = null,
    string? LotwRcvd = null,
    bool QrzSynced = false,
    bool QrzConfirmed = false
);

public record StationInfoDto(
    string? Name,
    string? Grid,
    string? Country,
    int? Dxcc,
    string? State,
    string? Continent,
    double? Latitude,
    double? Longitude
);

public record QsoSearchRequest(
    string? Callsign = null,
    string? Name = null,
    string? Band = null,
    string? Mode = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int? Dxcc = null,
    int Limit = 50,
    int Skip = 0,
    // Only QSOs in this logbook; null = all logbooks
    string? Logbook = null
);

public record PaginatedQsoResponse(
    IEnumerable<QsoResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages
);

public record QsoStatistics(
    int TotalQsos,
    int UniqueCallsigns,
    int UniqueCountries,
    int UniqueGrids,
    int QsosToday,
    Dictionary<string, int> QsosByBand,
    Dictionary<string, int> QsosByMode
);

/// <summary>Worked/confirmed status for one thing (a callsign, a grid, or a DXCC entity).</summary>
public record WorkedStatus(
    string? Key,
    int QsoCount,
    bool Worked,
    bool WorkedOnBand,
    bool WorkedOnMode,
    bool WorkedOnBandMode,
    bool Confirmed,
    bool ConfirmedOnBand,
    bool ConfirmedOnMode,
    bool ConfirmedOnBandMode,
    IReadOnlyList<string> BandsWorked,
    IReadOnlyList<string> BandsConfirmed,
    IReadOnlyList<string> ModesWorked
);

public record WorkedBeforeResponse(
    string Callsign,
    string? Band,
    string? Mode,
    string? Logbook,
    WorkedStatus Call,
    WorkedStatus? Grid,
    WorkedStatus? Entity,
    // Most recent previous QSO with this callsign, if any
    QsoResponse? LastQso
);
