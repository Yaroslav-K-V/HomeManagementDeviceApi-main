using System.Text.Json;
using HomeManagement.Shared;
using HomeManagementDashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace HomeManagementDashboard.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DevicesController(DeviceMonitorService monitorService) : ControllerBase
{
    public record RegisterDeviceRequest(string EndpointUrl);

    public record DiscoverDevicesRequest(string? Host = null, int? StartPort = null, int? EndPort = null);

    public record ExecuteCommandRequest(string? ActionName = null, string? FileName = null, string[]? Arguments = null);

    public record DeviceDto(
        int Id,
        string Name,
        string EndpointUrl,
        string ReportedAddress,
        bool IsOnline,
        long? ResponseTimeMs,
        DateTime? LastCheckedUtc,
        DateTime? LastOnlineUtc,
        Dictionary<string, string>? Metrics,
        IList<DeviceAction>? Actions,
        string? LastError);

    public record CommandExecutionResponse(
        int DeviceId,
        string DeviceName,
        string ActionName,
        string Result,
        DateTime ExecutedAtUtc);

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DeviceDto>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DeviceDto>> GetAll()
    {
        var devices = monitorService.GetDevices().Select(ToDto).ToList();
        return Ok(devices);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DeviceDto> GetById(int id)
    {
        var device = monitorService.GetDeviceById(id);
        if (device is null)
        {
            return NotFound(new { error = $"Device {id} not found." });
        }

        return Ok(ToDto(device));
    }

    [HttpPost]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceDto>> Register([FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EndpointUrl))
        {
            return BadRequest(new { error = "EndpointUrl is required." });
        }

        var device = monitorService.AddOrGetDevice(request.EndpointUrl);
        if (device is null)
        {
            return Conflict(new { error = "Endpoint is invalid or already registered." });
        }

        await monitorService.RefreshDeviceAsync(device, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = device.Id }, ToDto(device));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(int id)
    {
        if (!monitorService.RemoveDeviceById(id))
        {
            return NotFound(new { error = $"Device {id} not found." });
        }

        return NoContent();
    }

    [HttpPost("{id:int}/refresh")]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDto>> Refresh(int id, CancellationToken cancellationToken)
    {
        var device = monitorService.GetDeviceById(id);
        if (device is null)
        {
            return NotFound(new { error = $"Device {id} not found." });
        }

        await monitorService.RefreshDeviceAsync(device, cancellationToken);
        return Ok(ToDto(device));
    }

    [HttpPost("discover")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discover([FromBody] DiscoverDevicesRequest? request, CancellationToken cancellationToken)
    {
        var result = await monitorService.DiscoverDevicesAsync(
            request?.Host,
            request?.StartPort,
            request?.EndPort,
            cancellationToken);

        return Ok(new
        {
            probedEndpoints = result.ProbedCount,
            newlyDiscoveredCount = result.DiscoveredDevices.Count,
            newlyDiscovered = result.DiscoveredDevices.Select(ToDto).ToList(),
            totalManagedCount = monitorService.GetDevices().Count
        });
    }

    [HttpPost("{id:int}/commands")]
    [ProducesResponseType<CommandExecutionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommandExecutionResponse>> ExecuteCommand(
        int id,
        [FromBody] ExecuteCommandRequest request,
        CancellationToken cancellationToken)
    {
        var device = monitorService.GetDeviceById(id);
        if (device is null)
        {
            return NotFound(new { error = $"Device {id} not found." });
        }

        DeviceAction? action = null;
        if (!string.IsNullOrWhiteSpace(request.ActionName))
        {
            action = device.Config?.Actions.FirstOrDefault(a =>
                string.Equals(a.Action, request.ActionName, StringComparison.OrdinalIgnoreCase))
                ?? BuildStandardAction(request.ActionName);
        }
        else if (!string.IsNullOrWhiteSpace(request.FileName))
        {
            var payload = JsonSerializer.Serialize(new
            {
                FileName = request.FileName,
                Arguments = request.Arguments ?? []
            });
            action = new DeviceAction($"CUSTOM:{request.FileName}", CommandType.Post, "command", payload);
        }

        if (action is null)
        {
            return BadRequest(new { error = "Provide either ActionName (e.g. INFO, RESTART, SHUTDOWN) or FileName." });
        }

        var response = await monitorService.ExecuteActionAsync(device, action, cancellationToken);
        return Ok(new CommandExecutionResponse(
            device.Id,
            device.DisplayName,
            action.Action,
            response,
            device.LastActionTimeUtc ?? DateTime.UtcNow));
    }

    [HttpGet("{id:int}/logs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLogs(int id, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
    {
        var device = monitorService.GetDeviceById(id);
        if (device is null)
        {
            return NotFound(new { error = $"Device {id} not found." });
        }

        var logs = await monitorService.GetCommandLogsAsync(id, Math.Clamp(take, 1, 200), cancellationToken);
        return Ok(logs.Select(l => new
        {
            l.Id,
            l.ManagedDeviceId,
            l.ActionName,
            l.CommandType,
            l.TargetRoute,
            l.CommandArgs,
            l.ResponseMessage,
            l.Success,
            l.ExecutedAtUtc
        }));
    }

    private static DeviceAction? BuildStandardAction(string actionName)
    {
        return actionName.ToUpperInvariant() switch
        {
            "INFO" => new DeviceAction("INFO", CommandType.Get, "info"),
            "SHUTDOWN" => new DeviceAction("SHUTDOWN", CommandType.Post, "shutdown"),
            "RESTART" => new DeviceAction("RESTART", CommandType.Post, "restart"),
            _ => null
        };
    }

    private static DeviceDto ToDto(MonitoredDeviceState d) => new(
        d.Id,
        d.DisplayName,
        d.EndpointUrl,
        d.ReportedAddress,
        d.IsOnline,
        d.ResponseTimeMs,
        d.LastCheckedUtc,
        d.LastOnlineUtc,
        d.Info?.Metrics,
        d.Config?.Actions,
        d.LastError);
}
