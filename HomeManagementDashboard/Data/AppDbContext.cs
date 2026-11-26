using Microsoft.EntityFrameworkCore;

namespace HomeManagementDashboard.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ManagedDeviceEntity> Devices => Set<ManagedDeviceEntity>();
    public DbSet<DeviceCommandLogEntity> CommandLogs => Set<DeviceCommandLogEntity>();
    public DbSet<WorkflowRuleEntity> WorkflowRules => Set<WorkflowRuleEntity>();
    public DbSet<WorkflowLogEntity> WorkflowLogs => Set<WorkflowLogEntity>();

    public void EnsureSchema()
    {
        Database.EnsureCreated();
        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "WorkflowRules" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_WorkflowRules" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "TriggerType" INTEGER NOT NULL,
                "TriggerDeviceUrl" TEXT NOT NULL,
                "TriggerValue" REAL NULL,
                "ConditionType" INTEGER NOT NULL,
                "ConditionDeviceUrl" TEXT NULL,
                "ConditionValue" REAL NULL,
                "ActionDeviceUrl" TEXT NOT NULL,
                "ActionName" TEXT NOT NULL,
                "LastTriggeredUtc" TEXT NULL,
                "TriggerCount" INTEGER NOT NULL,
                "LastResult" TEXT NULL
            );
            """);
        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "WorkflowLogs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkflowLogs" PRIMARY KEY AUTOINCREMENT,
                "WorkflowRuleId" TEXT NULL,
                "RuleName" TEXT NOT NULL,
                "Message" TEXT NOT NULL,
                "Success" INTEGER NOT NULL,
                "TimestampUtc" TEXT NOT NULL
            );
            """);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagedDeviceEntity>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => d.EndpointUrl).IsUnique();
            entity.Property(d => d.EndpointUrl).IsRequired().HasMaxLength(512);
            entity.Property(d => d.Name).HasMaxLength(200);
            entity.Property(d => d.ReportedAddress).HasMaxLength(200);
        });

        modelBuilder.Entity<DeviceCommandLogEntity>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.ActionName).IsRequired().HasMaxLength(100);
            entity.Property(l => l.CommandType).IsRequired().HasMaxLength(20);
            entity.Property(l => l.TargetRoute).IsRequired().HasMaxLength(200);
            entity.HasOne(l => l.ManagedDevice)
                .WithMany(d => d.CommandLogs)
                .HasForeignKey(l => l.ManagedDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowRuleEntity>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Name).IsRequired().HasMaxLength(200);
            entity.Property(r => r.TriggerDeviceUrl).IsRequired().HasMaxLength(512);
            entity.Property(r => r.ActionDeviceUrl).IsRequired().HasMaxLength(512);
            entity.Property(r => r.ActionName).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<WorkflowLogEntity>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.RuleName).IsRequired().HasMaxLength(200);
            entity.Property(l => l.Message).IsRequired();
        });
    }
}
