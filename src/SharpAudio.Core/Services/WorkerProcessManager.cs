// wiki: backend-architecture#worker-process-lifecycle
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SharpAudio.Core.Worker;

namespace SharpAudio.Core.Services;

/// <summary>
/// Manages worker process lifecycle - spawning, tracking, and cleanup. One instance is created
/// per task type (TTS, ASR, music, diarization) so each can be configured/pooled independently.
/// </summary>
public sealed partial class WorkerProcessManager : IHostedService, IDisposable
{
    // The byte-stable stdout contract with every worker (see WorkerHost). Generated so the handshake
    // does not pay for an interpreted regex or the static Regex cache lookup on each output line.
    [GeneratedRegex(@"READY:(\d+)")]
    private static partial Regex ReadyPattern();

    private readonly WorkerTaskOptions _options;
    private readonly WorkerHealthProbe? _healthProbe;
    private readonly ILogger<WorkerProcessManager> _logger;
    private readonly ConcurrentDictionary<string, WorkerProcess> _workers = new();
    private readonly SemaphoreSlim _portAllocationLock = new(1, 1);
    private readonly HashSet<int> _allocatedPorts = new();
    private bool _disposed;

    public WorkerProcessManager(
        WorkerTaskOptions options,
        ILogger<WorkerProcessManager> logger,
        WorkerHealthProbe? healthProbe = null)
    {
        _options = options;
        _logger = logger;
        _healthProbe = healthProbe;
    }

    /// <summary>
    /// Get or spawn a worker for the given model
    /// </summary>
    public async Task<WorkerProcess> GetOrSpawnWorkerAsync(string modelKey, CancellationToken cancellationToken = default)
    {
        // Check if worker already exists and is healthy
        if (_workers.TryGetValue(modelKey, out var existingWorker))
        {
            if (existingWorker.IsAlive && await IsConsideredHealthyAsync(existingWorker, cancellationToken))
            {
                return existingWorker;
            }

            // Worker died (or is alive but cannot serve), remove it
            _logger.LogWarning("Worker for {ModelKey} is dead or unhealthy, respawning", modelKey);
            await RemoveWorkerAsync(modelKey);
        }

        // Spawn new worker
        return await SpawnWorkerAsync(modelKey, cancellationToken);
    }

    /// <summary>
    /// Liveness decision: process exit is always authoritative, and the HealthCheck RPC result is
    /// reused until the probe interval passes so probes do not add a round trip to every request.
    /// </summary>
    private async Task<bool> IsConsideredHealthyAsync(WorkerProcess worker, CancellationToken cancellationToken)
    {
        if (_healthProbe is null)
        {
            return true;
        }

        if (DateTime.UtcNow - worker.LastHealthProbeAt < TimeSpan.FromSeconds(_options.HealthProbeIntervalSeconds))
        {
            return worker.LastProbeHealthy;
        }

        var response = await _healthProbe.ProbeAsync(worker.ModelKey, worker.Port, cancellationToken);
        worker.LastHealthProbeAt = DateTime.UtcNow;
        worker.LastProbeHealthy = response is { IsReady: true };
        return worker.LastProbeHealthy;
    }

    private async Task<WorkerProcess> SpawnWorkerAsync(string modelKey, CancellationToken cancellationToken)
    {
        var port = await AllocatePortAsync(cancellationToken);

        try
        {
            _logger.LogInformation("Spawning worker for {ModelKey} on port {Port}", modelKey, port);

            var startInfo = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                Arguments = $"--port {port} --model-key \"{modelKey}\" --idle-timeout {_options.IdleTimeoutSeconds}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment =
                {
                    // Pass through necessary environment variables. The model cache and the native
                    // library paths are how a child worker finds the weights and the ONNX/Whisper
                    // runtimes, so they have to reach the child process.
                    ["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"
                }
            };

            foreach (var name in new[] { "MODEL_CACHE_DIR", "LD_LIBRARY_PATH", "ESPEAK_DATA_DIR" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    startInfo.Environment[name] = value;
                }
            }

            var process = new Process { StartInfo = startInfo };
            
            // Set up event handlers before starting
            var readySignal = new TaskCompletionSource<int>();
            var outputLog = new List<string>();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                
                outputLog.Add(e.Data);
                _logger.LogDebug("[Worker {ModelKey}] {Output}", modelKey, e.Data);

                // Look for READY signal
                var match = ReadyPattern().Match(e.Data);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var readyPort))
                {
                    readySignal.TrySetResult(readyPort);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    _logger.LogError("[Worker {ModelKey}] {Error}", modelKey, e.Data);
                }
            };

            // Start process
            if (!process.Start())
            {
                throw new Exception("Failed to start worker process");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for ready signal with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var readyPort = await readySignal.Task.WaitAsync(linkedCts.Token);
                
                if (readyPort != port)
                {
                    _logger.LogWarning("Worker reported different port {ReadyPort} than allocated {Port}", readyPort, port);
                }
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"Worker failed to start within {_options.StartupTimeoutSeconds} seconds. Output: {string.Join("\n", outputLog)}");
            }

            // Create worker entry
            var worker = new WorkerProcess(modelKey, process, port);
            _workers[modelKey] = worker;

            // Monitor process exit
            _ = MonitorWorkerExitAsync(worker);

            _logger.LogInformation("Worker for {ModelKey} started successfully on port {Port}", modelKey, port);
            return worker;
        }
        catch
        {
            // Release port on failure
            await ReleasePortAsync(port);
            throw;
        }
    }

    private async Task MonitorWorkerExitAsync(WorkerProcess worker)
    {
        try
        {
            await worker.Process.WaitForExitAsync();
            
            _logger.LogInformation("Worker {ModelKey} exited with code {ExitCode}", 
                worker.ModelKey, worker.Process.ExitCode);

            // Clean up
            await RemoveWorkerAsync(worker.ModelKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring worker {ModelKey}", worker.ModelKey);
        }
    }

    private async Task RemoveWorkerAsync(string modelKey)
    {
        if (_workers.TryRemove(modelKey, out var worker))
        {
            try
            {
                if (!worker.Process.HasExited)
                {
                    worker.Process.Kill(entireProcessTree: true);
                    await worker.Process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error killing worker {ModelKey}", modelKey);
            }
            finally
            {
                worker.Process.Dispose();
                await ReleasePortAsync(worker.Port);
            }
        }
    }

    private async Task<int> AllocatePortAsync(CancellationToken cancellationToken)
    {
        await _portAllocationLock.WaitAsync(cancellationToken);
        try
        {
            for (int attempt = 0; attempt < _options.MaxPortAttempts; attempt++)
            {
                var port = _options.PortRangeStart + attempt;
                
                if (!_allocatedPorts.Contains(port))
                {
                    _allocatedPorts.Add(port);
                    return port;
                }
            }

            throw new Exception($"Failed to allocate port after {_options.MaxPortAttempts} attempts");
        }
        finally
        {
            _portAllocationLock.Release();
        }
    }

    private async Task ReleasePortAsync(int port)
    {
        await _portAllocationLock.WaitAsync();
        try
        {
            _allocatedPorts.Remove(port);
        }
        finally
        {
            _portAllocationLock.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker process manager started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping all worker processes");

        var tasks = _workers.Keys.Select(key => RemoveWorkerAsync(key)).ToList();
        await Task.WhenAll(tasks);
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var worker in _workers.Values)
        {
            try
            {
                if (!worker.Process.HasExited)
                {
                    worker.Process.Kill(entireProcessTree: true);
                }
                worker.Process.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _workers.Clear();
        _portAllocationLock.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Represents a running worker process
/// </summary>
public sealed class WorkerProcess
{
    public string ModelKey { get; }
    public Process Process { get; }
    public int Port { get; }
    public DateTime SpawnedAt { get; }

    /// <summary>When the last HealthCheck probe for this worker ran, and what it concluded.</summary>
    public DateTime LastHealthProbeAt { get; set; }
    public bool LastProbeHealthy { get; set; } = true;

    public bool IsAlive => !Process.HasExited;

    public WorkerProcess(string modelKey, Process process, int port)
    {
        ModelKey = modelKey;
        Process = process;
        Port = port;
        SpawnedAt = DateTime.UtcNow;
    }
}
