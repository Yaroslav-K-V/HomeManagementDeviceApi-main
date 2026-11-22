using System.Net.Http.Json;
using System.Text;
using HomeManagement.Shared;

namespace HomeManagementDashboard.Services;

public class MonitoredDeviceState
{
    public required string EndpointUrl { get; set; }
    public string DisplayName => Info?.Name ?? Config?.Name ?? EndpointUrl;
    public string ReportedAddress => Info?.Address ?? Config?.Address ?? "—";
    public bool IsOnline { get; set; }
    public bool IsChecking { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastOnlineUtc { get; set; }
    public long? ResponseTimeMs { get; set; }
    public DeviceInfo? Info { get; set; }
    public DeviceConfig? Config { get; set; }
    public string? LastError { get; set; }
    public string? LastActionResponse { get; set; }
    public DateTime? LastActionTimeUtc { get; set; }
}

public class DeviceMonitorService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly List<MonitoredDeviceState> _devices = [];
    private readonly Lock _lock = new();

    public DeviceMonitorService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;

        var configuredEndpoints = configuration.GetSection("DeviceEndpoints").Get<string[]>()
            ?? ["http://localhost:1235", "http://localhost:1237"];

        foreach (var endpoint in configuredEndpoints)
        {
            AddDeviceEndpoint(endpoint);
        }
    }

    public IReadOnlyList<MonitoredDeviceState> GetDevices()
    {
        lock (_lock)
        {
            return _devices.ToList();
        }
    }

    public bool AddDeviceEndpoint(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var normalized = NormalizeUrl(rawUrl);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return false;
        }

        lock (_lock)
        {
            if (_devices.Any(d => string.Equals(d.EndpointUrl, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _devices.Add(new MonitoredDeviceState
            {
                EndpointUrl = normalized
            });
            return true;
        }
    }

    public bool RemoveDeviceEndpoint(string endpointUrl)
    {
        lock (_lock)
        {
            var existing = _devices.FirstOrDefault(d =>
                string.Equals(d.EndpointUrl, endpointUrl, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return false;
            }

            _devices.Remove(existing);
            return true;
        }
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = GetDevices();
        var tasks = snapshot.Select(d => RefreshDeviceAsync(d, cancellationToken));
        await Task.WhenAll(tasks);
    }

    public async Task RefreshDeviceAsync(MonitoredDeviceState device, CancellationToken cancellationToken = default)
    {
        device.IsChecking = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var client = _httpClientFactory.CreateClient("DeviceClient");
            var baseUrl = device.EndpointUrl.TrimEnd('/');

            var infoTask = client.GetFromJsonAsync<DeviceInfo>($"{baseUrl}/info", cancellationToken);
            var configTask = TryGetConfigAsync(client, $"{baseUrl}/config", cancellationToken);

            var info = await infoTask;
            var config = await configTask;
            sw.Stop();

            if (info is not null)
            {
                device.Info = info;
                if (config is not null)
                {
                    device.Config = config;
                }

                device.IsOnline = true;
                device.ResponseTimeMs = sw.ElapsedMilliseconds;
                device.LastCheckedUtc = DateTime.UtcNow;
                device.LastOnlineUtc = DateTime.UtcNow;
                device.LastError = null;
            }
            else
            {
                MarkOffline(device, "Empty response from /info endpoint.");
            }
        }
        catch (Exception ex)
        {
            MarkOffline(device, ex.Message);
        }
        finally
        {
            device.IsChecking = false;
        }
    }

    public async Task<string> ExecuteActionAsync(
        MonitoredDeviceState device,
        DeviceAction action,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("DeviceClient");
            var baseUrl = device.EndpointUrl.TrimEnd('/');
            var route = action.Command.TrimStart('/');
            var targetUrl = $"{baseUrl}/{route}";

            HttpResponseMessage response;
            if (action.CommandType == CommandType.Get)
            {
                response = await client.GetAsync(targetUrl, cancellationToken);
            }
            else
            {
                HttpContent content = !string.IsNullOrWhiteSpace(action.CommandArgs)
                    ? new StringContent(action.CommandArgs, Encoding.UTF8, "application/json")
                    : new StringContent(string.Empty, Encoding.UTF8, "application/json");

                response = await client.PostAsync(targetUrl, content, cancellationToken);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = response.IsSuccessStatusCode
                ? (string.IsNullOrWhiteSpace(body)
                    ? $"Action '{action.Action}' completed ({(int)response.StatusCode})."
                    : body)
                : $"HTTP {(int)response.StatusCode}: {body}";

            device.LastActionResponse = $"[{action.Action}] {message}";
            device.LastActionTimeUtc = DateTime.UtcNow;

            await RefreshDeviceAsync(device, cancellationToken);
            return device.LastActionResponse;
        }
        catch (Exception ex)
        {
            var err = $"[{action.Action}] Error: {ex.Message}";
            device.LastActionResponse = err;
            device.LastActionTimeUtc = DateTime.UtcNow;
            return err;
        }
    }

    private static async Task<DeviceConfig?> TryGetConfigAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetFromJsonAsync<DeviceConfig>(url, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static void MarkOffline(MonitoredDeviceState device, string error)
    {
        device.IsOnline = false;
        device.ResponseTimeMs = null;
        device.LastCheckedUtc = DateTime.UtcNow;
        device.LastError = error;
    }

    private static string NormalizeUrl(string rawUrl)
    {
        var trimmed = rawUrl.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"http://{trimmed}";
        }

        return trimmed;
    }
}
