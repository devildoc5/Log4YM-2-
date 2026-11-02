using Microsoft.AspNetCore.Mvc;
using Log4YM.Contracts.Models;
using Log4YM.Server.Core.Database;
using Log4YM.Server.Services;

namespace Log4YM.Server.Controllers;

/// <summary>Automatic logging from WSJT-X / JTDX / MSHV over UDP.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class WsjtxController : ControllerBase
{
    private readonly WsjtxUdpService _service;
    private readonly ISettingsRepository _settingsRepository;

    public WsjtxController(WsjtxUdpService service, ISettingsRepository settingsRepository)
    {
        _service = service;
        _settingsRepository = settingsRepository;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(WsjtxStatus), StatusCodes.Status200OK)]
    public ActionResult<WsjtxStatus> Status() => Ok(_service.GetStatus());

    /// <summary>Save listener settings and restart it with them.</summary>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(WsjtxStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WsjtxStatus>> SaveSettings([FromBody] WsjtxSettings request, CancellationToken cancellationToken)
    {
        if (request.Port is < 1 or > 65535)
            return BadRequest("Port must be between 1 and 65535.");
        if (!System.Net.IPAddress.TryParse(request.Address ?? "0.0.0.0", out _))
            return BadRequest("Address must be an IP address, e.g. 0.0.0.0 or 224.0.0.73.");

        var forward = (request.ForwardTo ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        var bad = forward.FirstOrDefault(t => WsjtxUdpService.ParseTargets(new[] { t }).Count == 0);
        if (bad != null)
            return BadRequest($"Forward target '{bad}' should look like 127.0.0.1:2238.");

        var settings = await _settingsRepository.GetAsync() ?? new UserSettings();
        settings.Wsjtx = new WsjtxSettings
        {
            Enabled = request.Enabled,
            Address = string.IsNullOrWhiteSpace(request.Address) ? "0.0.0.0" : request.Address.Trim(),
            Port = request.Port,
            ForwardTo = forward,
        };
        await _settingsRepository.UpsertAsync(settings);

        await _service.ReloadAsync(cancellationToken);

        // Give the socket a moment to bind (or fail) so the status we return is meaningful
        await Task.Delay(250, cancellationToken);
        return Ok(_service.GetStatus());
    }
}
