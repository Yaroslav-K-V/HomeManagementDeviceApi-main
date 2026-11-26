using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeManagement.Shared;
using HomeManagementDashboard.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeManagementDashboard.Services;

public class MonitoredDeviceState
{
    public int Id { get; set; }
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

public record DiscoveryResult(IReadOnlyList<MonitoredDeviceState> DiscoveredDevices, int ProbedCount);

public class DeviceMonitorService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly List<MonitoredDeviceState> _devices = [];
    private readonly Lock _lock = new();

    public DeviceMonitorService(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _configuration = configuration;

        InitializeFromDatabase();
    }

    private void InitializeFromDatabase()
    {
        using var db = _dbFactory.CreateDbContext();
        db.EnsureSchema();

        var existing = db.Devices.OrderBy(d => d.Id).ToList();
        if (existing.Count == 0)
        {
            var configuredEndpoints = _configuration.GetSection("DeviceEndpoints").Get<string[]>()
                ?? ["http://localhost:1235", "http://localhost:1237"];

            foreach (var rawEndpoint in configuredEndpoints)
            {
                var normalized = NormalizeUrl(rawEndpoint);
                if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
                {
                    continue;
                }

                if (existing.Any(d => string.Equals(d.EndpointUrl, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var entity = new ManagedDeviceEntity
                {
                    EndpointUrl = normalized,
                    CreatedAtUtc = DateTime.UtcNow
                };
                db.Devices.Add(entity);
                existing.Add(entity);
            }

            db.SaveChanges();
        }

        lock (_lock)
        {
            _devices.Clear();
            foreach (var entity in existing)
            {
                _devices.Add(MapToState(entity));
            }
        }
    }

    public IReadOnlyList<MonitoredDeviceState> GetDevices()
    {
        lock (_lock)
        {
            return _devices.ToList();
        }
    }

    public MonitoredDeviceState? GetDeviceById(int id)
    {
        lock (_lock)
        {
            return _devices.FirstOrDefault(d => d.Id == id);
        }
    }

    public bool AddDeviceEndpoint(string rawUrl)
    {
        return AddOrGetDevice(rawUrl) is not null;
    }

    public MonitoredDeviceState? AddOrGetDevice(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var normalized = NormalizeUrl(rawUrl);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return null;
        }

        lock (_lock)
        {
            if (_devices.Any(d => string.Equals(d.EndpointUrl, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        using var db = _dbFactory.CreateDbContext();
        if (db.Devices.Any(d => d.EndpointUrl.ToLower() == normalized.ToLower()))
        {
            return null;
        }

        var entity = new ManagedDeviceEntity
        {
            EndpointUrl = normalized,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Devices.Add(entity);
        db.SaveChanges();

        var state = MapToState(entity);
        lock (_lock)
        {
            if (_devices.Any(d => string.Equals(d.EndpointUrl, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            _devices.Add(state);
            return state;
        }
    }

    public bool RemoveDeviceEndpoint(string endpointUrl)
    {
        MonitoredDeviceState? existing;
        lock (_lock)
        {
            existing = _devices.FirstOrDefault(d =>
                string.Equals(d.EndpointUrl, endpointUrl, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return false;
            }

            _devices.Remove(existing);
        }

        using var db = _dbFactory.CreateDbContext();
        var entity = db.Devices.FirstOrDefault(d => d.Id == existing.Id);
        if (entity is not null)
        {
            db.Devices.Remove(entity);
            db.SaveChanges();
        }

        return true;
    }

    public bool RemoveDeviceById(int id)
    {
        MonitoredDeviceState? existing;
        lock (_lock)
        {
            existing = _devices.FirstOrDefault(d => d.Id == id);
            if (existing is null)
            {
                return false;
            }

            _devices.Remove(existing);
        }

        using var db = _dbFactory.CreateDbContext();
        var entity = db.Devices.FirstOrDefault(d => d.Id == id);
        if (entity is not null)
        {
            db.Devices.Remove(entity);
            db.SaveChanges();
        }

        return true;
    }

    public async Task<DiscoveryResult> DiscoverDevicesAsync(
        string? host = null,
        int? startPort = null,
        int? endPort = null,
        CancellationToken cancellationToken = default)
    {
        var targetHost = string.IsNullOrWhiteSpace(host)
            ? _configuration.GetValue("Discovery:Host", "localhost")
            : host.Trim();
        var portStart = startPort ?? _configuration.GetValue("Discovery:StartPort", 1235);
        var portEnd = endPort ?? _configuration.GetValue("Discovery:EndPort", 1250);

        if (portEnd < portStart)
        {
            (portStart, portEnd) = (portEnd, portStart);
        }

        // Cap scan range to prevent accidental huge port scans.
        portEnd = Math.Min(portEnd, portStart + 100);

        var ports = Enumerable.Range(portStart, portEnd - portStart + 1).ToArray();
        var probeTasks = ports.Select(port => ProbeEndpointAsync($"http://{targetHost}:{port}", cancellationToken));
        var results = await Task.WhenAll(probeTasks);

        var newlyAdded = new List<MonitoredDeviceState>();
        foreach (var probe in results.Where(r => r is not null))
        {
            var (endpointUrl, info, config, elapsedMs) = probe!.Value;
            var added = AddOrGetDevice(endpointUrl);
            if (added is not null)
            {
                added.Info = info;
                added.Config = config;
                added.IsOnline = true;
                added.ResponseTimeMs = elapsedMs;
                added.LastCheckedUtc = DateTime.UtcNow;
                added.LastOnlineUtc = DateTime.UtcNow;
                added.LastError = null;
                PersistDeviceState(added);
                newlyAdded.Add(added);
            }
            else
            {
                // Update existing tracked device if it responded during discovery.
                MonitoredDeviceState? existing;
                lock (_lock)
                {
                    existing = _devices.FirstOrDefault(d =>
                        string.Equals(d.EndpointUrl, endpointUrl, StringComparison.OrdinalIgnoreCase));
                }

                if (existing is not null)
                {
                    existing.Info = info;
                    if (config is not null)
                    {
                        existing.Config = config;
                    }
                    existing.IsOnline = true;
                    existing.ResponseTimeMs = elapsedMs;
                    existing.LastCheckedUtc = DateTime.UtcNow;
                    existing.LastOnlineUtc = DateTime.UtcNow;
                    existing.LastError = null;
                    PersistDeviceState(existing);
                }
            }
        }

        return new DiscoveryResult(newlyAdded, ports.Length);
    }

    private async Task<(string EndpointUrl, DeviceInfo Info, DeviceConfig? Config, long ElapsedMs)?> ProbeEndpointAsync(
        string endpointUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(600));

            using var client = _httpClientFactory.CreateClient("DeviceClient");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var info = await client.GetFromJsonAsync<DeviceInfo>($"{endpointUrl}/info", cts.Token);
            if (info is null || string.IsNullOrWhiteSpace(info.Name))
            {
                return null;
            }

            var config = await TryGetConfigAsync(client, $"{endpointUrl}/config", cts.Token);
            sw.Stop();
            return (endpointUrl, info, config, sw.ElapsedMilliseconds);
        }
        catch
        {
            return null;
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
            PersistDeviceState(device);
        }
    }

    public async Task<string> ExecuteActionAsync(
        MonitoredDeviceState device,
        DeviceAction action,
        CancellationToken cancellationToken = default)
    {
        bool success = false;
        string rawMessage;

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
            success = response.IsSuccessStatusCode;
            rawMessage = response.IsSuccessStatusCode
                ? (string.IsNullOrWhiteSpace(body)
                    ? $"Action '{action.Action}' completed ({(int)response.StatusCode})."
                    : body)
                : $"HTTP {(int)response.StatusCode}: {body}";

            device.LastActionResponse = $"[{action.Action}] {rawMessage}";
            device.LastActionTimeUtc = DateTime.UtcNow;

            SaveCommandLog(device.Id, action, rawMessage, success);
            await RefreshDeviceAsync(device, cancellationToken);
            return device.LastActionResponse;
        }
        catch (Exception ex)
        {
            rawMessage = ex.Message;
            var err = $"[{action.Action}] Error: {ex.Message}";
            device.LastActionResponse = err;
            device.LastActionTimeUtc = DateTime.UtcNow;
            SaveCommandLog(device.Id, action, rawMessage, success: false);
            return err;
        }
    }

    public async Task<IReadOnlyList<DeviceCommandLogEntity>> GetCommandLogsAsync(int deviceId, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CommandLogs
            .Where(l => l.ManagedDeviceId == deviceId)
            .OrderByDescending(l => l.ExecutedAtUtc)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private void SaveCommandLog(int deviceId, DeviceAction action, string responseMessage, bool success)
    {
        if (deviceId <= 0)
        {
            return;
        }

        try
        {
            using var db = _dbFactory.CreateDbContext();
            db.CommandLogs.Add(new DeviceCommandLogEntity
            {
                ManagedDeviceId = deviceId,
                ActionName = action.Action,
                CommandType = action.CommandType.ToString(),
                TargetRoute = action.Command,
                CommandArgs = action.CommandArgs,
                ResponseMessage = responseMessage,
                Success = success,
                ExecutedAtUtc = DateTime.UtcNow
            });
            db.SaveChanges();
        }
        catch
        {
            // Avoid failing live command execution on logging errors.
        }
    }

    private void PersistDeviceState(MonitoredDeviceState device)
    {
        if (device.Id <= 0)
        {
            return;
        }

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = db.Devices.FirstOrDefault(d => d.Id == device.Id);
            if (entity is null)
            {
                return;
            }

            entity.Name = device.Info?.Name ?? device.Config?.Name ?? entity.Name;
            entity.ReportedAddress = device.Info?.Address ?? device.Config?.Address ?? entity.ReportedAddress;
            entity.IsOnline = device.IsOnline;
            entity.ResponseTimeMs = device.ResponseTimeMs;
            entity.LastCheckedUtc = device.LastCheckedUtc;
            entity.LastOnlineUtc = device.LastOnlineUtc;
            entity.LastError = device.LastError;

            if (device.Info?.Metrics is not null)
            {
                entity.LastMetricsJson = JsonSerializer.Serialize(device.Info.Metrics);
            }

            if (device.Config?.Actions is not null)
            {
                entity.LastActionsJson = JsonSerializer.Serialize(device.Config.Actions);
            }

            db.SaveChanges();
        }
        catch
        {
            // Keep polling resilient on transient SQLite lock contention.
        }
    }

    private static MonitoredDeviceState MapToState(ManagedDeviceEntity entity)
    {
        Dictionary<string, string>? metrics = null;
        if (!string.IsNullOrWhiteSpace(entity.LastMetricsJson))
        {
            try
            {
                metrics = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.LastMetricsJson);
            }
            catch
            {
                // Ignore malformed cached JSON.
            }
        }

        List<DeviceAction>? actions = null;
        if (!string.IsNullOrWhiteSpace(entity.LastActionsJson))
        {
            try
            {
                actions = JsonSerializer.Deserialize<List<DeviceAction>>(entity.LastActionsJson);
            }
            catch
            {
                // Ignore malformed cached JSON.
            }
        }

        DeviceInfo? info = null;
        if (!string.IsNullOrWhiteSpace(entity.Name) || metrics is not null)
        {
            info = new DeviceInfo
            {
                Name = entity.Name ?? entity.EndpointUrl,
                Address = entity.ReportedAddress ?? "—",
                Metrics = metrics ?? new Dictionary<string, string>()
            };
        }

        DeviceConfig? config = null;
        if (actions is not null)
        {
            config = new DeviceConfig
            {
                Name = entity.Name ?? entity.EndpointUrl,
                Address = entity.ReportedAddress ?? "—",
                Actions = actions
            };
        }

        return new MonitoredDeviceState
        {
            Id = entity.Id,
            EndpointUrl = entity.EndpointUrl,
            IsOnline = entity.IsOnline,
            ResponseTimeMs = entity.ResponseTimeMs,
            LastCheckedUtc = entity.LastCheckedUtc,
            LastOnlineUtc = entity.LastOnlineUtc,
            LastError = entity.LastError,
            Info = info,
            Config = config
        };
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
