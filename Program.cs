using HomeManagement.Shared;
using HomeManagementDeviceApi;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CommandsSettings>(builder.Configuration.GetSection("CommandsSettings"));

var app = builder.Build();

app.UseHttpsRedirection();

app.MapGet("/info", (IConfiguration configuration) => new DeviceInfo
{
    Name = configuration.GetValue("DeviceName", string.Empty),
    Address = NetworkManager.GetAddress(),
    Metrics = new Dictionary<string, string>
    {
        { "UptimeSeconds", DeviceManager.GetUptime().ToString() },
        { "Temperature", DeviceManager.GetTemperature().ToString(CultureInfo.InvariantCulture) }
    }
});
app.MapGet("/config", (IConfiguration configuration, IOptions<CommandsSettings> commandsOptions) =>
{
    var actions = new List<DeviceAction>()
    {
        new ("INFO", CommandType.Get, "info"),
        new ("SHUTDOWN", CommandType.Post, "shutdown"),
        new ("RESTART", CommandType.Post, "restart")
    };
    actions.AddRange(commandsOptions.Value.Commands.Select(x => new DeviceAction(x.Name, CommandType.Post, "command", JsonSerializer.Serialize(x.Command))));

    return new DeviceConfig
    {
        Name = configuration.GetValue("DeviceName", string.Empty),
        Address = NetworkManager.GetAddress(),
        Actions = actions
    };
});
app.MapPost("/command", async (Command command, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(command.FileName))
    {
        return "Command file name is required.";
    }

    var arguments = string.Join(' ', command.Arguments);
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        var result = process.ExitCode == 0 ? await outputTask : await errorTask;
        return string.IsNullOrWhiteSpace(result)
            ? $"Command '{command.FileName}' executed successfully (exit code {process.ExitCode})."
            : result;
    }
    catch (Exception ex)
    {
        return $"Failed to execute '{command.FileName}': {ex.Message}";
    }
});

app.MapPost("/shutdown", () =>
{
    try
    {
        Process.Start("poweroff");
        return "Shutdown initiated.";
    }
    catch (Exception ex)
    {
        return $"Shutdown command received (simulated: {ex.Message})";
    }
});

app.MapPost("/restart", () =>
{
    try
    {
        Process.Start("reboot");
        return "Restart initiated.";
    }
    catch (Exception ex)
    {
        return $"Restart command received (simulated: {ex.Message})";
    }
});

app.Run();