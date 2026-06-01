using JiraGitLabSync.Configuration;
using JiraGitLabSync.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiraGitLabSync.Workers;

/// <summary>
/// Long-running Windows Service worker.
/// Runs a Jira → GitLab sync on a configurable interval.
/// </summary>
public sealed class SyncWorker : BackgroundService
{
    private readonly ISyncOrchestrator _orchestrator;
    private readonly SyncSettings _settings;
    private readonly ILogger<SyncWorker> _log;

    public SyncWorker(
        ISyncOrchestrator orchestrator,
        IOptions<SyncSettings> settings,
        ILogger<SyncWorker> log)
    {
        _orchestrator = orchestrator;
        _settings     = settings.Value;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "JiraGitLabSync service started. Sync interval: {Interval} minute(s).",
            _settings.SyncIntervalMinutes);

        // Run one sync immediately on start, then repeat on the interval.
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunWithRetryAsync(stoppingToken);

            _log.LogInformation(
                "Next sync in {Minutes} minute(s) at ~{NextRun:HH:mm:ss} UTC.",
                _settings.SyncIntervalMinutes,
                DateTime.UtcNow.AddMinutes(_settings.SyncIntervalMinutes));

            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(_settings.SyncIntervalMinutes),
                    stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Service stopping — exit gracefully
                break;
            }
        }

        _log.LogInformation("JiraGitLabSync service is stopping.");
    }

    private async Task RunWithRetryAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= _settings.MaxRetryAttempts; attempt++)
        {
            try
            {
                await _orchestrator.RunSyncAsync(ct);
                return; // success
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("Sync cancelled during attempt {Attempt}.", attempt);
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Sync attempt {Attempt}/{Max} failed: {Message}",
                    attempt, _settings.MaxRetryAttempts, ex.Message);

                if (attempt == _settings.MaxRetryAttempts)
                {
                    _log.LogError("All retry attempts exhausted. Waiting until next scheduled run.");
                    return;
                }

                var delay = TimeSpan.FromSeconds(_settings.RetryDelaySeconds * attempt);
                _log.LogInformation("Retrying in {Delay} seconds…", delay.TotalSeconds);

                try { await Task.Delay(delay, ct); }
                catch (TaskCanceledException) { return; }
            }
        }
    }
}
