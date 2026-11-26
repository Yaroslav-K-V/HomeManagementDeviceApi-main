using System.Globalization;
using System.Text.Json;
using HomeManagement.Shared;
using HomeManagementDashboard.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeManagementDashboard.Services;

public enum WorkflowTriggerType
{
    DeviceOnline,
    DeviceOffline,
    TemperatureAbove,
    TemperatureBelow,
    UptimeAbove,
    OnPoll
}

public enum WorkflowConditionType
{
    None,
    DeviceIsOnline,
    DeviceIsOffline,
    TemperatureAbove,
    TemperatureBelow,
    OnlineCountAtLeast,
    OnlineCountBelow
}

public class WorkflowRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Workflow";
    public bool Enabled { get; set; } = true;

    // Trigger
    public WorkflowTriggerType TriggerType { get; set; } = WorkflowTriggerType.TemperatureAbove;
    public string TriggerDeviceUrl { get; set; } = "*";
    public double? TriggerValue { get; set; } = 45.0;

    // Condition
    public WorkflowConditionType ConditionType { get; set; } = WorkflowConditionType.None;
    public string? ConditionDeviceUrl { get; set; }
    public double? ConditionValue { get; set; }

    // Action
    public string ActionDeviceUrl { get; set; } = "http://localhost:1235";
    public string ActionName { get; set; } = "INFO";

    // Runtime state
    public DateTime? LastTriggeredUtc { get; set; }
    public int TriggerCount { get; set; }
    public string? LastResult { get; set; }
}

public record WorkflowLogEntry(DateTime TimestampUtc, string RuleName, string Message, bool Success);

public class WorkflowEngineService : BackgroundService
{
    private readonly DeviceMonitorService _monitorService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly string _legacyJsonPath;
    private readonly List<WorkflowRule> _rules = [];
    private readonly List<WorkflowLogEntry> _logs = [];
    private readonly Dictionary<string, bool> _previousOnlineStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private static readonly TimeSpan RuleCooldown = TimeSpan.FromSeconds(30);

    public event Action? OnStateChanged;

    public WorkflowEngineService(
        DeviceMonitorService monitorService,
        IDbContextFactory<AppDbContext> dbFactory,
        IHostEnvironment env)
    {
        _monitorService = monitorService;
        _dbFactory = dbFactory;
        _legacyJsonPath = Path.Combine(env.ContentRootPath, "workflows.json");
        LoadFromDatabase();
    }

    public IReadOnlyList<WorkflowRule> GetRules()
    {
        lock (_lock)
        {
            return _rules.ToList();
        }
    }

    public IReadOnlyList<WorkflowLogEntry> GetLogs()
    {
        lock (_lock)
        {
            return _logs.ToList();
        }
    }

    public void AddRule(WorkflowRule rule)
    {
        lock (_lock)
        {
            _rules.Add(rule);
        }

        using var db = _dbFactory.CreateDbContext();
        db.WorkflowRules.Add(ToEntity(rule));
        db.SaveChanges();

        OnStateChanged?.Invoke();
    }

    public void ToggleRule(Guid id)
    {
        WorkflowRule? updated = null;
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule is not null)
            {
                rule.Enabled = !rule.Enabled;
                updated = rule;
            }
        }

        if (updated is not null)
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = db.WorkflowRules.FirstOrDefault(r => r.Id == id);
            if (entity is not null)
            {
                entity.Enabled = updated.Enabled;
                db.SaveChanges();
            }
        }

        OnStateChanged?.Invoke();
    }

    public void DeleteRule(Guid id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _rules.RemoveAll(r => r.Id == id) > 0;
        }

        if (removed)
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = db.WorkflowRules.FirstOrDefault(r => r.Id == id);
            if (entity is not null)
            {
                db.WorkflowRules.Remove(entity);
                db.SaveChanges();
            }
        }

        OnStateChanged?.Invoke();
    }

    public void ClearLogs()
    {
        lock (_lock)
        {
            _logs.Clear();
        }

        using var db = _dbFactory.CreateDbContext();
        db.WorkflowLogs.ExecuteDelete();

        OnStateChanged?.Invoke();
    }

    public async Task<string> RunRuleNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        WorkflowRule? rule;
        lock (_lock)
        {
            rule = _rules.FirstOrDefault(r => r.Id == id);
        }

        if (rule is null)
        {
            return "Rule not found.";
        }

        var devices = _monitorService.GetDevices();
        if (!EvaluateCondition(rule, devices))
        {
            var skipped = "Condition not met; action skipped.";
            RecordResult(rule, skipped, success: false);
            return skipped;
        }

        var result = await ExecuteRuleActionAsync(rule, devices, cancellationToken);
        OnStateChanged?.Invoke();
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Keep background loop alive on transient errors.
            }
        }
    }

    public async Task EvaluateAllAsync(CancellationToken cancellationToken = default)
    {
        await _monitorService.RefreshAllAsync(cancellationToken);
        var devices = _monitorService.GetDevices();
        var rules = GetRules();
        var now = DateTime.UtcNow;

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (rule.TriggerType != WorkflowTriggerType.DeviceOnline &&
                rule.TriggerType != WorkflowTriggerType.DeviceOffline &&
                rule.LastTriggeredUtc.HasValue &&
                now - rule.LastTriggeredUtc.Value < RuleCooldown)
            {
                continue;
            }

            if (EvaluateTrigger(rule, devices) && EvaluateCondition(rule, devices))
            {
                await ExecuteRuleActionAsync(rule, devices, cancellationToken);
            }
        }

        foreach (var d in devices)
        {
            _previousOnlineStates[d.EndpointUrl] = d.IsOnline;
        }

        OnStateChanged?.Invoke();
    }

    private bool EvaluateTrigger(WorkflowRule rule, IReadOnlyList<MonitoredDeviceState> devices)
    {
        var targets = string.Equals(rule.TriggerDeviceUrl, "*", StringComparison.Ordinal)
            ? devices
            : devices.Where(d => string.Equals(d.EndpointUrl, rule.TriggerDeviceUrl, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var device in targets)
        {
            var hadPrev = _previousOnlineStates.TryGetValue(device.EndpointUrl, out var wasOnline);

            switch (rule.TriggerType)
            {
                case WorkflowTriggerType.DeviceOnline:
                    if (hadPrev && !wasOnline && device.IsOnline)
                    {
                        return true;
                    }
                    break;

                case WorkflowTriggerType.DeviceOffline:
                    if (hadPrev && wasOnline && !device.IsOnline)
                    {
                        return true;
                    }
                    break;

                case WorkflowTriggerType.TemperatureAbove:
                    if (device.IsOnline && TryGetMetric(device, "Temperature", out var tempHigh) &&
                        tempHigh > (rule.TriggerValue ?? 0))
                    {
                        return true;
                    }
                    break;

                case WorkflowTriggerType.TemperatureBelow:
                    if (device.IsOnline && TryGetMetric(device, "Temperature", out var tempLow) &&
                        tempLow < (rule.TriggerValue ?? 0))
                    {
                        return true;
                    }
                    break;

                case WorkflowTriggerType.UptimeAbove:
                    if (device.IsOnline && TryGetMetric(device, "UptimeSeconds", out var uptime) &&
                        uptime > (rule.TriggerValue ?? 0))
                    {
                        return true;
                    }
                    break;

                case WorkflowTriggerType.OnPoll:
                    return true;
            }
        }

        return false;
    }

    private static bool EvaluateCondition(WorkflowRule rule, IReadOnlyList<MonitoredDeviceState> devices)
    {
        var condDevice = !string.IsNullOrWhiteSpace(rule.ConditionDeviceUrl)
            ? devices.FirstOrDefault(d => string.Equals(d.EndpointUrl, rule.ConditionDeviceUrl, StringComparison.OrdinalIgnoreCase))
            : null;

        return rule.ConditionType switch
        {
            WorkflowConditionType.None => true,
            WorkflowConditionType.DeviceIsOnline => condDevice?.IsOnline == true,
            WorkflowConditionType.DeviceIsOffline => condDevice?.IsOnline == false,
            WorkflowConditionType.TemperatureAbove => condDevice is { IsOnline: true } &&
                TryGetMetric(condDevice, "Temperature", out var tHigh) && tHigh > (rule.ConditionValue ?? 0),
            WorkflowConditionType.TemperatureBelow => condDevice is { IsOnline: true } &&
                TryGetMetric(condDevice, "Temperature", out var tLow) && tLow < (rule.ConditionValue ?? 0),
            WorkflowConditionType.OnlineCountAtLeast => devices.Count(d => d.IsOnline) >= (int)(rule.ConditionValue ?? 1),
            WorkflowConditionType.OnlineCountBelow => devices.Count(d => d.IsOnline) < (int)(rule.ConditionValue ?? 1),
            _ => false
        };
    }

    private async Task<string> ExecuteRuleActionAsync(
        WorkflowRule rule,
        IReadOnlyList<MonitoredDeviceState> devices,
        CancellationToken cancellationToken)
    {
        var targetDevice = devices.FirstOrDefault(d =>
            string.Equals(d.EndpointUrl, rule.ActionDeviceUrl, StringComparison.OrdinalIgnoreCase));

        if (targetDevice is null)
        {
            var msg = $"Target device '{rule.ActionDeviceUrl}' not found.";
            RecordResult(rule, msg, success: false);
            return msg;
        }

        var action = targetDevice.Config?.Actions.FirstOrDefault(a =>
            string.Equals(a.Action, rule.ActionName, StringComparison.OrdinalIgnoreCase))
            ?? BuildFallbackAction(rule.ActionName);

        var response = await _monitorService.ExecuteActionAsync(targetDevice, action, cancellationToken);
        var ok = !response.Contains("Error:", StringComparison.OrdinalIgnoreCase) &&
                 !response.Contains("HTTP 4", StringComparison.OrdinalIgnoreCase) &&
                 !response.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase);

        RecordResult(rule, response, ok);
        return response;
    }

    private static DeviceAction BuildFallbackAction(string actionName)
    {
        return actionName.ToUpperInvariant() switch
        {
            "INFO" => new DeviceAction("INFO", CommandType.Get, "info"),
            "SHUTDOWN" => new DeviceAction("SHUTDOWN", CommandType.Post, "shutdown"),
            "RESTART" => new DeviceAction("RESTART", CommandType.Post, "restart"),
            _ => new DeviceAction(actionName, CommandType.Get, "info")
        };
    }

    private void RecordResult(WorkflowRule rule, string message, bool success)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            rule.LastTriggeredUtc = now;
            rule.TriggerCount++;
            rule.LastResult = message;

            _logs.Insert(0, new WorkflowLogEntry(now, rule.Name, message, success));
            if (_logs.Count > 50)
            {
                _logs.RemoveRange(50, _logs.Count - 50);
            }
        }

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = db.WorkflowRules.FirstOrDefault(r => r.Id == rule.Id);
            if (entity is not null)
            {
                entity.LastTriggeredUtc = rule.LastTriggeredUtc;
                entity.TriggerCount = rule.TriggerCount;
                entity.LastResult = rule.LastResult;
            }

            db.WorkflowLogs.Add(new WorkflowLogEntity
            {
                WorkflowRuleId = rule.Id,
                RuleName = rule.Name,
                Message = message,
                Success = success,
                TimestampUtc = now
            });

            db.SaveChanges();
        }
        catch
        {
            // Keep background workflow loop resilient on transient SQLite lock contention.
        }
    }

    private static bool TryGetMetric(MonitoredDeviceState device, string key, out double value)
    {
        value = 0;
        return device.Info?.Metrics.TryGetValue(key, out var raw) == true &&
               double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private void LoadFromDatabase()
    {
        using var db = _dbFactory.CreateDbContext();
        db.EnsureSchema();

        var ruleEntities = db.WorkflowRules.AsNoTracking().ToList();
        if (ruleEntities.Count == 0)
        {
            var seeded = TryLoadLegacyRules() ??
            [
                new WorkflowRule
                {
                    Name = "High Temp Check",
                    Enabled = false,
                    TriggerType = WorkflowTriggerType.TemperatureAbove,
                    TriggerDeviceUrl = "http://localhost:1235",
                    TriggerValue = 45.0,
                    ConditionType = WorkflowConditionType.OnlineCountAtLeast,
                    ConditionValue = 1,
                    ActionDeviceUrl = "http://localhost:1235",
                    ActionName = "INFO"
                }
            ];

            foreach (var r in seeded)
            {
                var entity = ToEntity(r);
                db.WorkflowRules.Add(entity);
                ruleEntities.Add(entity);
            }

            db.SaveChanges();
        }

        var logEntities = db.WorkflowLogs
            .OrderByDescending(l => l.TimestampUtc)
            .Take(50)
            .AsNoTracking()
            .ToList();

        lock (_lock)
        {
            _rules.Clear();
            _rules.AddRange(ruleEntities.Select(ToModel));

            _logs.Clear();
            _logs.AddRange(logEntities.Select(l => new WorkflowLogEntry(l.TimestampUtc, l.RuleName, l.Message, l.Success)));
        }
    }

    private List<WorkflowRule>? TryLoadLegacyRules()
    {
        if (!File.Exists(_legacyJsonPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_legacyJsonPath);
            var loaded = JsonSerializer.Deserialize<List<WorkflowRule>>(json);
            return loaded is { Count: > 0 } ? loaded : null;
        }
        catch
        {
            return null;
        }
    }

    private static WorkflowRuleEntity ToEntity(WorkflowRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Enabled = rule.Enabled,
        TriggerType = (int)rule.TriggerType,
        TriggerDeviceUrl = rule.TriggerDeviceUrl,
        TriggerValue = rule.TriggerValue,
        ConditionType = (int)rule.ConditionType,
        ConditionDeviceUrl = rule.ConditionDeviceUrl,
        ConditionValue = rule.ConditionValue,
        ActionDeviceUrl = rule.ActionDeviceUrl,
        ActionName = rule.ActionName,
        LastTriggeredUtc = rule.LastTriggeredUtc,
        TriggerCount = rule.TriggerCount,
        LastResult = rule.LastResult
    };

    private static WorkflowRule ToModel(WorkflowRuleEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Enabled = entity.Enabled,
        TriggerType = (WorkflowTriggerType)entity.TriggerType,
        TriggerDeviceUrl = entity.TriggerDeviceUrl,
        TriggerValue = entity.TriggerValue,
        ConditionType = (WorkflowConditionType)entity.ConditionType,
        ConditionDeviceUrl = entity.ConditionDeviceUrl,
        ConditionValue = entity.ConditionValue,
        ActionDeviceUrl = entity.ActionDeviceUrl,
        ActionName = entity.ActionName,
        LastTriggeredUtc = entity.LastTriggeredUtc,
        TriggerCount = entity.TriggerCount,
        LastResult = entity.LastResult
    };
}
