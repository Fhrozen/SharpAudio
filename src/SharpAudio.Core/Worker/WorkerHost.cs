// wiki: worker-core#worker-bootstrap
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services;

namespace SharpAudio.Core.Worker;

/// <summary>
/// Shared bootstrap for every task worker executable: Kestrel HTTP/2 on localhost, gRPC, the idle
/// monitor, and the <c>READY:{port}</c> handshake. Each executable supplies only its own service
/// registrations and the gRPC service to map, so the bootstrap itself is written once.
/// </summary>
public static class WorkerHost
{
    public static async Task RunAsync<TService>(
        WorkerArguments arguments,
        string logPrefix,
        Action<IServiceCollection> configureServices,
        CancellationToken cancellationToken = default)
        where TService : class
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(options =>
            options.ListenLocalhost(arguments.Port, listenOptions =>
                listenOptions.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc();

        // Same options source as the API (appsettings section plus environment overrides), so a
        // worker process and the in-process path resolve the same policy.
        builder.Services.AddOptions<VadOptions>().Bind(builder.Configuration.GetSection(VadOptions.SectionName));

        // Idle shutdown goes through the host lifetime rather than Environment.Exit, so requests
        // already in flight are drained before the process ends.
        builder.Services.AddSingleton<IdleMonitor>(sp => new IdleMonitor(
            arguments.IdleTimeout,
            () => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication()));

        configureServices(builder.Services);

        var app = builder.Build();
        app.MapGrpcService<TService>();

        Console.WriteLine($"[{logPrefix}] Starting on port {arguments.Port} for model: {arguments.ModelKey}");
        Console.WriteLine($"[{logPrefix}] Idle timeout: {arguments.IdleTimeout.TotalSeconds} seconds");

        await app.StartAsync(cancellationToken);

        // Byte-stable contract read by WorkerProcessManager: READY:{port}
        Console.WriteLine($"[{logPrefix}] READY:{arguments.Port}");

        await app.WaitForShutdownAsync(cancellationToken);
    }
}
