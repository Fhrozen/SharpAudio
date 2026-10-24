// wiki: backend-architecture#core-abstractions
using SharpAudio.Core.Options;
using Microsoft.Extensions.Options;

namespace SharpAudio.Core.Services;

/// <summary>
/// Background service that releases idle engines from memory after a configurable timeout. One instance
/// is registered per task type (TTS, ASR, …) against an <see cref="IIdleTracking"/> source, so a new
/// task type needs no service of its own — only a descriptor and an aggregate. All task types share the
/// same <see cref="ModelIdleMonitorOptions"/> (MODEL_IDLE_TIMEOUT_SECONDS) configuration.
/// </summary>
public sealed class IdleMonitorService : BackgroundService
{
    private readonly ILogger<IdleMonitorService> _logger;
    private readonly IIdleTracking _tracking;
    private readonly ModelIdleMonitorOptions _options;
    private readonly string _label;

    public IdleMonitorService(
        ILogger<IdleMonitorService> logger,
        IOptions<ModelIdleMonitorOptions> options,
        IIdleTracking tracking,
        string label)
    {
        _logger = logger;
        _options = options.Value;
        _tracking = tracking;
        _label = label;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // If timeout is 0, monitoring is disabled
        if (_options.IdleTimeoutSeconds <= 0)
        {
            _logger.LogInformation("{Label} model idle monitoring is disabled (IdleTimeoutSeconds = 0)", _label);
            return;
        }

        _logger.LogInformation(
            "{Label} model idle monitoring started. Timeout: {TimeoutSeconds}s, Check interval: {IntervalSeconds}s",
            _label,
            _options.IdleTimeoutSeconds,
            _options.CheckIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
                CheckAndReleaseIdleEngines();
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in {Label} model idle monitoring", _label);
            }
        }

        _logger.LogInformation("{Label} model idle monitoring stopped", _label);
    }

    private void CheckAndReleaseIdleEngines()
    {
        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);

        foreach (var (engineKey, lastAccessTime) in _tracking.GetLoadedEngines())
        {
            var idleTime = now - lastAccessTime;
            if (idleTime <= timeout)
            {
                continue;
            }

            _logger.LogInformation(
                "Releasing idle {Label} model '{EngineKey}' (idle for {IdleSeconds:F1}s)",
                _label,
                engineKey,
                idleTime.TotalSeconds);

            if (_tracking.TryReleaseEngine(engineKey))
            {
                _logger.LogInformation("Successfully released {Label} model '{EngineKey}'", _label, engineKey);
            }
            else
            {
                _logger.LogWarning("Failed to release {Label} model '{EngineKey}'", _label, engineKey);
            }
        }
    }
}
