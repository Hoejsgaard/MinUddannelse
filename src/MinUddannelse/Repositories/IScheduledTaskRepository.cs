using MinUddannelse.Models;
using MinUddannelse.Repositories.DTOs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MinUddannelse.Repositories;

public interface IScheduledTaskRepository
{
    Task<List<ScheduledTask>> GetScheduledTasksAsync();
    Task<ScheduledTask?> GetScheduledTaskAsync(string name);
    Task<int> AddScheduledTaskAsync(ScheduledTask task);
    Task UpdateScheduledTaskAsync(ScheduledTask task);
}
