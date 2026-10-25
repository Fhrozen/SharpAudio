// wiki: diarization#worker-scaffold
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Diar.Services;

// Default port is the diarization range, distinct from the other task ranges.
var arguments = WorkerArguments.Parse(args, defaultPort: 50351, defaultIdleSeconds: 60);

await WorkerHost.RunAsync<WorkerDiarService>(
    arguments,
    logPrefix: "Worker.Diar",
    configureServices: services => services.AddSingleton<WorkerDiarService>());
