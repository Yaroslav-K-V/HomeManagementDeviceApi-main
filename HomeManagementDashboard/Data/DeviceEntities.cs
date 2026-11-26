namespace HomeManagementDashboard.Data;

public class ManagedDeviceEntity
{
    public int Id { get; set; }
    public required string EndpointUrl { get; set; }
    public string? Name { get; set; }
    public string? ReportedAddress { get; set; }
    public bool IsOnline { get; set; }
    public long? ResponseTimeMs { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastOnlineUtc { get; set; }
    public string? LastMetricsJson { get; set; }
    public string? LastActionsJson { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<DeviceCommandLogEntity> CommandLogs { get; set; } = [];
}

public class DeviceCommandLogEntity
{
    public int Id { get; set; }
    public int ManagedDeviceId { get; set; }
    public ManagedDeviceEntity? ManagedDevice { get; set; }
    public required string ActionName { get; set; }
    public required string CommandType { get; set; }
    public required string TargetRoute { get; set; }
    public string? CommandArgs { get; set; }
    public required string ResponseMessage { get; set; }
    public bool Success { get; set; }
    public DateTime ExecutedAtUtc { get; set; } = DateTime.UtcNow;
}

public class WorkflowRuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public int TriggerType { get; set; }
    public required string TriggerDeviceUrl { get; set; }
    public double? TriggerValue { get; set; }
    public int ConditionType { get; set; }
    public string? ConditionDeviceUrl { get; set; }
    public double? ConditionValue { get; set; }
    public required string ActionDeviceUrl { get; set; }
    public required string ActionName { get; set; }
    public DateTime? LastTriggeredUtc { get; set; }
    public int TriggerCount { get; set; }
    public string? LastResult { get; set; }
}

public class WorkflowLogEntity
{
    public int Id { get; set; }
    public Guid? WorkflowRuleId { get; set; }
    public required string RuleName { get; set; }
    public required string Message { get; set; }
    public bool Success { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
