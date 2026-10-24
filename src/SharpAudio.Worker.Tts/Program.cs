// wiki: backend-architecture#worker-entry-points
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Tts.Services;

var arguments = WorkerArguments.Parse(args, defaultPort: 50051, defaultIdleSeconds: 60);

await WorkerHost.RunAsync<WorkerSynthesisService>(
    arguments,
    logPrefix: "Worker.Tts",
    configureServices: services => services.AddSingleton<WorkerSynthesisService>());
