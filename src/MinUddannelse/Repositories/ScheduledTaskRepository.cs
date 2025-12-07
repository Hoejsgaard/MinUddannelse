using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using Microsoft.Extensions.Logging;
using Supabase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MinUddannelse.Repositories;

public class ScheduledTaskRepository : IScheduledTaskRepository
{
    private readonly Supabase.Client _supabase;
    private readonly ILogger _logger;

    public ScheduledTaskRepository(Supabase.Client supabase, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(supabase);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _supabase = supabase;
        _logger = loggerFactory.CreateLogger<ScheduledTaskRepository>();
    }

    public async Task<List<ScheduledTask>> GetScheduledTasksAsync()
    {
        var tasksResponse = await _supabase
            .From<ScheduledTask>()
            .Where(t => t.Enabled == true)
            .Get();

        return tasksResponse.Models;
    }

    public async Task<ScheduledTask?> GetScheduledTaskAsync(string name)
    {
        var result = await _supabase
            .From<ScheduledTask>()
            .Select("*")
            .Where(st => st.Name == name)
            .Get();

        return result.Models.FirstOrDefault();
    }

    public async Task<int> AddScheduledTaskAsync(ScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.CronExpression);

        // Use UTC time consistently - Supabase stores DateTime as UTC
        var now = DateTime.UtcNow;
        task.CreatedAt = now;
        task.UpdatedAt = now;

        // Initialize LastRun to current time to prevent immediate execution
        // This ensures new scheduled tasks don't fire immediately upon creation
        if (task.LastRun == null)
        {
            task.LastRun = now;
            _logger.LogInformation("Initialized LastRun to {LastRun} for new scheduled task: {TaskName}", task.LastRun, task.Name);
        }

        var insertResponse = await _supabase
            .From<ScheduledTask>()
            .Insert(task);

        var insertedTask = insertResponse.Models.FirstOrDefault();
        if (insertedTask == null)
        {
            throw new InvalidOperationException("Failed to insert scheduled task");
        }

        _logger.LogInformation("Added scheduled task with ID {TaskId}: {TaskName}", insertedTask.Id, task.Name);
        return insertedTask.Id;
    }

    public async Task UpdateScheduledTaskAsync(ScheduledTask task)
    {
        task.UpdatedAt = DateTime.UtcNow;

        await _supabase
            .From<ScheduledTask>()
            .Where(t => t.Id == task.Id)
            .Update(task);

        _logger.LogInformation("Updated scheduled task: {TaskName}", task.Name);
    }
}
