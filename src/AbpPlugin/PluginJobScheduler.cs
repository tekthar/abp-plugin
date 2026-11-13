using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Volo.Abp.Uow;

namespace AbpPlugin.Hosting;

/// <summary>
/// Runs plugin recurring jobs on their cron schedules. Each occurrence runs once for the site: every server works out the
/// same slot, and the first to claim it runs it. A job never overlaps itself, on this server or
/// another (each run holds the site's lock for the job: a server that can't take it skips the run), and its
/// cancellation token fires when the plugin is disabled. Each run is one unit of work with the server's.
/// </summary>
public sealed class PluginJobScheduler(PluginRuntime runtime, AbpCluster.IClusterLocks locks, IPluginJobSlots slots, IUnitOfWorkManager unitOfWorks,
    ILogger<PluginJobScheduler> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTime> _nextRun = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            StartDueJobs(stoppingToken);
        }
    }

    // Kept synchronous on purpose: locals and closures in a long-running async method are hoisted into its
    // state machine, and one that captured a job registration would keep a disabled plugin's types alive.
    private void StartDueJobs(CancellationToken stoppingToken)
    {
        DateTime now = DateTime.UtcNow;
        foreach (LoadedPlugin plugin in runtime.Enabled)
        {
            foreach (RecurringJobRegistration job in plugin.Registrations.Jobs)
            {
                string id = $"{plugin.Key}@{plugin.Manifest.Version}:{job.JobType.FullName}";
                if (!_nextRun.TryGetValue(id, out DateTime due))
                {
                    due = job.Expression.GetNextOccurrence(now) ?? DateTime.MaxValue;
                    _nextRun[id] = due;
                }

                if (due > now || !_running.TryAdd(id, 0))
                {
                    continue;
                }

                _nextRun[id] = job.Expression.GetNextOccurrence(now) ?? DateTime.MaxValue;
                _ = RunAsync(plugin, job, id, due, stoppingToken);
            }
        }
    }

    private async Task RunAsync(LoadedPlugin plugin, RecurringJobRegistration job, string id, DateTime due, CancellationToken stoppingToken)
    {
        try
        {
            using IDisposable? use = plugin.TryBeginUse();
            if (use is null)
            {
                return;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, plugin.Stopping);
            // This occurrence once for the site: a short run would otherwise end before another server's tick reached the
            // same slot, and that server would run it again.
            if (!await slots.TryClaimAsync($"plugin-job:{plugin.Key}:{job.JobType.FullName}", due, linked.Token))
            {
                return;
            }

            // One server at a time, whichever version of the plugin it runs (an upgrade rolls through the servers).
            await using IAsyncDisposable? lease = await locks.TryTakeAsync($"plugin-job:{plugin.Key}:{job.JobType.FullName}", linked.Token);
            if (lease is null)
            {
                return;
            }

            await plugin.MeasureAsync($"job:{job.JobType.Name}", () =>
                PluginCalls.RunAsync(unitOfWorks, plugin, PluginCalls.IsTransactional(job.JobType), services =>
                    job.Execute(services.GetRequiredService(job.JobType), linked.Token)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plugin {Plugin} job {Job} failed", plugin.Key, job.JobType.Name);
        }
        finally
        {
            _running.TryRemove(id, out _);
        }
    }

    /// <summary>Forget schedule state for a plugin version so nothing keeps its job types alive.</summary>
    internal void Forget(LoadedPlugin plugin)
    {
        string prefix = $"{plugin.Key}@{plugin.Manifest.Version}:";
        foreach (string id in _nextRun.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _nextRun.TryRemove(id, out _);
        }
    }
}
