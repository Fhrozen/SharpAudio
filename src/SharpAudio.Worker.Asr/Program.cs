// wiki: asr-engines#worker-process-di-wiring-and-server-mode
using SharpAudio.Core.Services.Pnc;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Asr.Services;

// Default port is the ASR range, distinct from TTS's 50051+.
var arguments = WorkerArguments.Parse(args, defaultPort: 50151, defaultIdleSeconds: 60);

await WorkerHost.RunAsync<WorkerTranscriptionService>(
    arguments,
    logPrefix: "Worker.Asr",
    configureServices: services =>
    {
        services.AddSingleton<IPncModelCatalog, PncModelCatalog>();
        services.AddSingleton<IPncEnginePool, PncEnginePool>();
        services.AddSingleton<WorkerTranscriptionService>();
    });
