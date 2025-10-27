using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace MinUddannelse.Repositories.DTOs;

[Table("scheduled_tasks")]
public class ScheduledTask : BaseModel
{
    [PrimaryKey("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("cron_expression")]
    public string CronExpression { get; set; } = string.Empty;

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("retry_interval_hours")]
    public int? RetryIntervalHours { get; set; }

    [Column("max_retry_hours")]
    public int? MaxRetryHours { get; set; }

    [Column("last_run")]
    public DateTime? LastRun { get; set; }

    [Column("next_run")]
    public DateTime? NextRun { get; set; }

    [Column("task_type")]
    public string TaskType { get; set; } = "hardcoded";

    [Column("reminder_id")]
    public int? ReminderId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
