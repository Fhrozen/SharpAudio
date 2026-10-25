// wiki: music#worker-scaffold
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Music.Services;

// Default port is the music range, distinct from TTS's 50051+ and ASR's 50151+.
var arguments = WorkerArguments.Parse(args, defaultPort: 50251, defaultIdleSeconds: 60);

await WorkerHost.RunAsync<WorkerMusicService>(
    arguments,
    logPrefix: "Worker.Music",
    configureServices: services => services.AddSingleton<WorkerMusicService>());
