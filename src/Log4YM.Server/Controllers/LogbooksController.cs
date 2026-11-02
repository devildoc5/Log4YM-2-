using Microsoft.AspNetCore.Mvc;
using Log4YM.Contracts.Models;
using Log4YM.Server.Core.Database;

namespace Log4YM.Server.Controllers;

public record LogbookSummary(
    string Name,
    string? StationCallsign,
    string? TqslLocation,
    string? Description,
    int QsoCount,
    bool IsActive,
    bool IsDefined);

public record LogbooksResponse(string Active, IEnumerable<LogbookSummary> Logbooks);

public record SaveLogbooksRequest(string? Active, List<LogbookDefinition> Logbooks);

/// <summary>
/// Separate logbooks (e.g. a special-event station) that share one database. Every QSO carries
/// its logbook name; QSOs from before logbooks existed belong to "Main".
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class LogbooksController : ControllerBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IQsoRepository _qsoRepository;

    public LogbooksController(ISettingsRepository settingsRepository, IQsoRepository qsoRepository)
    {
        _settingsRepository = settingsRepository;
        _qsoRepository = qsoRepository;
    }

    /// <summary>All logbooks: the ones defined in Settings plus any found on QSOs (e.g. from an ADIF import), with counts.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(LogbooksResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LogbooksResponse>> Get()
    {
        var settings = await _settingsRepository.GetAsync() ?? new UserSettings();
        var defined = settings.Logbooks?.Logbooks ?? new List<LogbookDefinition>();
        var active = Logbooks.Normalize(settings.Logbooks?.Active);

        var counts = (await _qsoRepository.GetAllAsync())
            .GroupBy(q => Logbooks.Normalize(q.Logbook), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var names = new List<string> { Logbooks.Default };
        names.AddRange(defined.Select(d => Logbooks.Normalize(d.Name)));
        names.AddRange(counts.Keys);

        var summaries = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var def = defined.FirstOrDefault(d =>
                    string.Equals(Logbooks.Normalize(d.Name), name, StringComparison.OrdinalIgnoreCase));
                return new LogbookSummary(
                    name,
                    def?.StationCallsign,
                    def?.TqslLocation,
                    def?.Description,
                    counts.TryGetValue(name, out var c) ? c : 0,
                    string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
                    def != null || name.Equals(Logbooks.Default, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        return Ok(new LogbooksResponse(active, summaries));
    }

    /// <summary>Save logbook definitions and which one new QSOs go into.</summary>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save([FromBody] SaveLogbooksRequest request)
    {
        var cleaned = (request.Logbooks ?? new List<LogbookDefinition>())
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .Select(l => new LogbookDefinition
            {
                Name = l.Name.Trim(),
                StationCallsign = string.IsNullOrWhiteSpace(l.StationCallsign) ? null : l.StationCallsign.Trim().ToUpperInvariant(),
                TqslLocation = string.IsNullOrWhiteSpace(l.TqslLocation) ? null : l.TqslLocation.Trim(),
                Description = string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim(),
            })
            .ToList();

        var duplicate = cleaned
            .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            return BadRequest($"There are two logbooks named '{duplicate.Key}'.");

        var active = Logbooks.Normalize(request.Active);
        if (!active.Equals(Logbooks.Default, StringComparison.OrdinalIgnoreCase) &&
            !cleaned.Any(l => l.Name.Equals(active, StringComparison.OrdinalIgnoreCase)))
            return BadRequest($"The active logbook '{active}' isn't in the list.");

        var settings = await _settingsRepository.GetAsync() ?? new UserSettings();
        settings.Logbooks = new LogbookSettings { Active = active, Logbooks = cleaned };
        await _settingsRepository.UpsertAsync(settings);

        return Ok(new { message = "Logbooks saved" });
    }

    /// <summary>Switch which logbook new QSOs are logged into.</summary>
    [HttpPut("active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetActive([FromBody] SetActiveLogbookRequest request)
    {
        var settings = await _settingsRepository.GetAsync() ?? new UserSettings();
        settings.Logbooks ??= new LogbookSettings();
        var name = Logbooks.Normalize(request.Name);

        // Switching to a name that isn't defined yet creates it, so "start a new event log" is one step
        if (!name.Equals(Logbooks.Default, StringComparison.OrdinalIgnoreCase) &&
            !settings.Logbooks.Logbooks.Any(l => Logbooks.Normalize(l.Name).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            settings.Logbooks.Logbooks.Add(new LogbookDefinition { Name = name });
        }

        settings.Logbooks.Active = name;
        await _settingsRepository.UpsertAsync(settings);
        return Ok(new { active = name });
    }
}

public record SetActiveLogbookRequest(string? Name);
