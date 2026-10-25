// wiki: backend-architecture#request-flow
using SharpAudio.Core.Contracts;
using SharpAudio.Core.Health;
using SharpAudio.Core.Models;
using SharpAudio.Core.Options;
using SharpAudio.Core.Services;
using SharpAudio.Core.Services.Pnc;
using SharpAudio.Core.Worker;
using SharpAudio.Worker.Asr.Grpc;
using SharpAudio.Worker.Diar.Grpc;
using SharpAudio.Worker.Grpc;
using SharpAudio.Worker.Music.Grpc;
using Grpc.Net.Client;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

const string TtsWorkerKey = "tts";
const string AsrWorkerKey = "asr";
const string MusicWorkerKey = "music";
const string DiarWorkerKey = "diar";

// Per-task gRPC plumbing: one channel cache and health probe per worker task, so the
// TTS and ASR proxies never share connection state or message-size limits.
static Func<IServiceProvider, object, WorkerChannelCache> ChannelCacheFactory(WorkerTaskOptions options) =>
    (sp, key) => new WorkerChannelCache(
        sp.GetRequiredService<ILogger<WorkerChannelCache>>(),
        options.MaxReceiveMessageBytes,
        options.MaxSendMessageBytes);

static Func<IServiceProvider, object, WorkerHealthProbe> HealthProbeFactory(
    WorkerTaskOptions options,
    Func<GrpcChannel, HealthCheckRequest, CancellationToken, Task<HealthCheckResponse>> call) =>
    (sp, key) => new WorkerHealthProbe(
        sp.GetRequiredKeyedService<WorkerChannelCache>(key),
        sp.GetRequiredService<ILogger<WorkerHealthProbe>>(),
        options,
        call);

// Shared registration for every worker task: keyed cache, keyed probe, keyed process manager
// (and the hosted service that pre-spawns workers). Proxies resolve the same key with
// [FromKeyedServices], so each task keeps its own port range and message-size limits.
static void RegisterWorkerTask(IServiceCollection services, WorkerTaskDescriptor task)
{
    services.AddKeyedSingleton<WorkerChannelCache>(task.Key, ChannelCacheFactory(task.Options));
    services.AddKeyedSingleton<WorkerHealthProbe>(task.Key, HealthProbeFactory(task.Options, task.HealthCheckCall));
    services.AddKeyedSingleton<WorkerProcessManager>(task.Key, (sp, key) => new WorkerProcessManager(
        task.Options,
        sp.GetRequiredService<ILogger<WorkerProcessManager>>(),
        sp.GetRequiredKeyedService<WorkerHealthProbe>(key)));
    services.AddHostedService(sp => sp.GetRequiredKeyedService<WorkerProcessManager>(task.Key));
}

// Configure URLs from environment variables
var httpPort = Environment.GetEnvironmentVariable("HTTP_PORT") ?? "8080";
var httpsPort = Environment.GetEnvironmentVariable("HTTPS_PORT");

var urls = new List<string> { $"http://+:{httpPort}" };
if (!string.IsNullOrWhiteSpace(httpsPort))
{
    urls.Add($"https://+:{httpsPort}");
    // Kestrel loads the actual certificate from Kestrel:Certificates:Default config (env vars
    // Kestrel__Certificates__Default__Path/Password) - no certificate-loading code needed here.
    builder.Services.AddHttpsRedirection(options => options.HttpsPort = int.Parse(httpsPort));
}

builder.WebHost.UseUrls(urls.ToArray());

// SERVER_MODE: tts (default, preserves pre-ASR behavior) | asr | music | diar | both (tts+asr) | all.
var serverModeRaw = Environment.GetEnvironmentVariable("SERVER_MODE");
var mode = ServerMode.Parse(serverModeRaw);
var serverMode = (serverModeRaw ?? "tts").Trim().ToLowerInvariant();
Console.WriteLine($"[SharpAudio] SERVER_MODE={serverMode} (tts={mode.Tts}, asr={mode.Asr}, music={mode.Music}, diar={mode.Diar})");

builder.Services.Configure<ModelCacheOptions>(builder.Configuration.GetSection(ModelCacheOptions.SectionName));
builder.Services.Configure<ModelIdleMonitorOptions>(builder.Configuration.GetSection(ModelIdleMonitorOptions.SectionName));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.Configure<AsrWorkerOptions>(builder.Configuration.GetSection(AsrWorkerOptions.SectionName));
builder.Services.Configure<AsrStreamingOptions>(builder.Configuration.GetSection(AsrStreamingOptions.SectionName));
builder.Services.Configure<MusicWorkerOptions>(builder.Configuration.GetSection(MusicWorkerOptions.SectionName));
builder.Services.Configure<DiarWorkerOptions>(builder.Configuration.GetSection(DiarWorkerOptions.SectionName));
builder.Services.Configure<VadOptions>(builder.Configuration.GetSection(VadOptions.SectionName));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IModelCache, ModelCache>();

if (mode.Tts)
{
    builder.Services.AddSingleton<IModelCatalog, ModelCatalog>();

    // Configure synthesizer based on worker mode
    var workerOptions = builder.Configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();

    if (workerOptions.Enabled)
    {
        // Worker process mode - use proxy synthesizer
        Console.WriteLine("[SharpAudio] TTS worker mode ENABLED - models will run in separate processes");
        RegisterWorkerTask(builder.Services, new WorkerTaskDescriptor(
            TtsWorkerKey,
            workerOptions,
            (channel, request, ct) => new WorkerSynthesis.WorkerSynthesisClient(channel)
                .HealthCheckAsync(request, cancellationToken: ct).ResponseAsync));
        // Registering the concrete type lets warmup reach it as a warmer as well as a synthesizer:
        // in worker mode warming a model means pre-spawning the process that owns its engine.
        builder.Services.AddSingleton<WorkerProxySynthesizer>();
        builder.Services.AddSingleton<ITtsSynthesizer>(sp => sp.GetRequiredService<WorkerProxySynthesizer>());
        builder.Services.AddSingleton<IEngineWarmer<TtsModelDefinition>>(sp => sp.GetRequiredService<WorkerProxySynthesizer>());
    }
    else
    {
        // In-process mode - use direct synthesizers
        Console.WriteLine("[SharpAudio] TTS worker mode DISABLED - models will run in-process");
        builder.Services.AddSingleton<KokoroTtsSynthesizer>();
        builder.Services.AddSingleton<SupertonicTtsSynthesizer>();
        builder.Services.AddSingleton<TtsSynthesizerRouter>(sp => new TtsSynthesizerRouter(
            sp.GetRequiredService<KokoroTtsSynthesizer>(),
            sp.GetRequiredService<SupertonicTtsSynthesizer>()));
        builder.Services.AddSingleton<ITtsSynthesizer>(sp => sp.GetRequiredService<TtsSynthesizerRouter>());
        builder.Services.AddSingleton<IEngineWarmer<TtsModelDefinition>>(sp => sp.GetRequiredService<TtsSynthesizerRouter>());
        builder.Services.AddHostedService(sp => new IdleMonitorService(
            sp.GetRequiredService<ILogger<IdleMonitorService>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelIdleMonitorOptions>>(),
            sp.GetRequiredService<TtsSynthesizerRouter>(),
            "TTS"));
    }

    builder.Services.AddHostedService(sp => new ModelWarmupService<TtsModelDefinition>(
        sp.GetRequiredService<IModelCatalog>(),
        sp.GetRequiredService<IModelCache>(),
        sp.GetRequiredService<ILoggerFactory>(),
        "TTS",
        sp.GetRequiredService<IEngineWarmer<TtsModelDefinition>>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelCacheOptions>>()));
}

if (mode.Asr)
{
    builder.Services.AddSingleton<IAsrModelCatalog, AsrModelCatalog>();
    builder.Services.AddSingleton<IPncModelCatalog, PncModelCatalog>();
        builder.Services.AddSingleton<IPncEnginePool, PncEnginePool>();
    var asrWorkerOptions = builder.Configuration.GetSection(AsrWorkerOptions.SectionName).Get<AsrWorkerOptions>() ?? new AsrWorkerOptions();

    if (asrWorkerOptions.Enabled)
    {
        // Worker process mode - use proxy transcriber, on its own keyed WorkerProcessManager
        // instance/port range so it can run alongside the TTS worker without colliding.
        Console.WriteLine("[SharpAudio] ASR worker mode ENABLED - models will run in separate processes");
        RegisterWorkerTask(builder.Services, new WorkerTaskDescriptor(
            AsrWorkerKey,
            asrWorkerOptions,
            (channel, request, ct) => new WorkerTranscription.WorkerTranscriptionClient(channel)
                .HealthCheckAsync(request, cancellationToken: ct).ResponseAsync));
        // Registering the concrete type lets warmup reach it as a warmer as well as a transcriber:
        // in worker mode warming a model means pre-spawning the process that owns its engine.
        builder.Services.AddSingleton<AsrWorkerProxyTranscriber>();
        builder.Services.AddSingleton<IAsrTranscriber>(sp => sp.GetRequiredService<AsrWorkerProxyTranscriber>());
        builder.Services.AddSingleton<IEngineWarmer<AsrModelDefinition>>(sp => sp.GetRequiredService<AsrWorkerProxyTranscriber>());
    }
    else
    {
        // In-process mode - use direct transcribers
        Console.WriteLine("[SharpAudio] ASR worker mode DISABLED - models will run in-process");
        builder.Services.AddSingleton(sp => new WhisperAsrTranscriber(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VadOptions>>().Value));
        builder.Services.AddSingleton(sp => new NemotronAsrTranscriber(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VadOptions>>().Value));
        builder.Services.AddSingleton<AsrTranscriberRouter>(sp => new AsrTranscriberRouter(
            sp.GetRequiredService<WhisperAsrTranscriber>(),
            sp.GetRequiredService<NemotronAsrTranscriber>()));
        builder.Services.AddSingleton<IAsrTranscriber>(sp => new PunctuatingTranscriber(
            sp.GetRequiredService<AsrTranscriberRouter>(),
            sp.GetRequiredService<IPncModelCatalog>(),
            sp.GetRequiredService<IModelCache>(),
            sp.GetRequiredService<IPncEnginePool>()));
        builder.Services.AddSingleton<IEngineWarmer<AsrModelDefinition>>(sp => sp.GetRequiredService<AsrTranscriberRouter>());
        builder.Services.AddHostedService(sp => new IdleMonitorService(
            sp.GetRequiredService<ILogger<IdleMonitorService>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelIdleMonitorOptions>>(),
            new IdleTrackingAggregate(
                sp.GetRequiredService<AsrTranscriberRouter>(),
                sp.GetRequiredService<IPncEnginePool>()),
            "ASR"));
    }

    builder.Services.AddHostedService(sp => new ModelWarmupService<AsrModelDefinition>(
        sp.GetRequiredService<IAsrModelCatalog>(),
        sp.GetRequiredService<IModelCache>(),
        sp.GetRequiredService<ILoggerFactory>(),
        "ASR",
        sp.GetRequiredService<IEngineWarmer<AsrModelDefinition>>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelCacheOptions>>()));
}

// Music and diarization are worker-only tasks today: neither has an in-process engine, so the
// proxy is the whole path. No warmup service is registered because the model catalogs for these
// tasks arrive with their engine phases.
if (mode.Music)
{
    var musicWorkerOptions = builder.Configuration.GetSection(MusicWorkerOptions.SectionName).Get<MusicWorkerOptions>() ?? new MusicWorkerOptions();

    Console.WriteLine("[SharpAudio] Music worker mode ENABLED - models will run in separate processes");
    RegisterWorkerTask(builder.Services, new WorkerTaskDescriptor(
        MusicWorkerKey,
        musicWorkerOptions,
        (channel, request, ct) => new WorkerMusic.WorkerMusicClient(channel)
            .HealthCheckAsync(request, cancellationToken: ct).ResponseAsync));
    builder.Services.AddSingleton<MusicWorkerProxyGenerator>();
    builder.Services.AddSingleton<IMusicGenerator>(sp => sp.GetRequiredService<MusicWorkerProxyGenerator>());
}

if (mode.Diar)
{
    var diarWorkerOptions = builder.Configuration.GetSection(DiarWorkerOptions.SectionName).Get<DiarWorkerOptions>() ?? new DiarWorkerOptions();

    Console.WriteLine("[SharpAudio] Diarization worker mode ENABLED - models will run in separate processes");
    RegisterWorkerTask(builder.Services, new WorkerTaskDescriptor(
        DiarWorkerKey,
        diarWorkerOptions,
        (channel, request, ct) => new WorkerDiarization.WorkerDiarizationClient(channel)
            .HealthCheckAsync(request, cancellationToken: ct).ResponseAsync));
    builder.Services.AddSingleton<DiarWorkerProxyDiarizer>();
    builder.Services.AddSingleton<IDiarizer>(sp => sp.GetRequiredService<DiarWorkerProxyDiarizer>());
}

// Add Swagger/OpenAPI support
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "SharpAudio API",
        Version = "v1",
        Description = "Text-to-Speech REST API with OpenAI-compatible endpoints. Powered by Kokoro TTS models with custom OnnxRuntime inference and espeak-ng phonemization.",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "SharpAudio",
            Url = new Uri("https://github.com/yourusername/SharpAudio")
        }
    });

    // Include XML comments if available
    var xmlFilename = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(httpsPort))
{
    app.UseHttpsRedirection();
}

// Enable Swagger in all environments
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "SharpAudio API v1");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "SharpAudio API Documentation";
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("HealthCheck")
    .WithTags("Health")
    .WithSummary("Health check endpoint")
    .WithDescription("Returns the service health status")
    .Produces<object>(200);

app.MapGet("/api/server-info", () => Results.Ok(new ServerInfoResponse(mode.Tts, mode.Asr, mode.Music, mode.Diar)))
    .WithName("GetServerInfo")
    .WithTags("Health")
    .WithSummary("Get server capability info")
    .WithDescription("Returns which task types (TTS speech synthesis, ASR transcription) this server instance was started with, controlled by the SERVER_MODE environment variable")
    .Produces<ServerInfoResponse>(200);

if (mode.Tts)
{
app.MapGet("/api/models", (IModelCatalog modelCatalog) =>
{
    var models = modelCatalog.GetSupportedModels().Select(m =>
    {
        // Add speaker metadata for Supertonic models
        IReadOnlyList<SpeakerMetadata>? speakerMetadata = null;
        if (SupertonicMetadata.IsSupertonic3Engine(m.Engine))
        {
            speakerMetadata = m.Speakers
                .Select(speakerId => new SpeakerMetadata(
                    speakerId,
                    SupertonicMetadata.GetSpeakerName(speakerId),
                    SupertonicMetadata.GetSpeakerDescription(speakerId)))
                .ToArray();
        }

        return new ModelDefinitionResponse(
            m.Name,
            m.DisplayName,
            m.Description,
            m.SupportedLanguages,
            m.Speakers,
            speakerMetadata);
    });

    return Results.Ok(models);
})
    .WithName("GetModels")
    .WithTags("Models")
    .WithSummary("List available TTS models")
    .WithDescription("Returns detailed information about all available text-to-speech models, including supported languages and speakers")
    .Produces<IEnumerable<ModelDefinitionResponse>>(200);
}

// Both catalogs are singletons whose model list is built once at construction. Resolving them here
// captures them for the endpoint instead of doing a service lookup per request; each is only
// registered when its task type is enabled for this instance, so the captured value can be null.
var openAiSpeechTtsCatalog = app.Services.GetService<IModelCatalog>();
var openAiSpeechAsrCatalog = app.Services.GetService<IAsrModelCatalog>();

app.MapGet("/v1/models", () =>
{
    var models = new List<object>();

    if (openAiSpeechTtsCatalog is not null)
    {
        models.AddRange(openAiSpeechTtsCatalog.GetSupportedModels().Select(m => (object)new
        {
            id = m.Name,
            @object = "model",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            owned_by = "sharp-audio"
        }));
    }

    if (openAiSpeechAsrCatalog is not null)
    {
        models.AddRange(openAiSpeechAsrCatalog.GetSupportedModels().Select(m => (object)new
        {
            id = m.Name,
            @object = "model",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            owned_by = "sharp-audio"
        }));
    }

    return Results.Ok(new { data = models, @object = "list" });
})
    .WithName("ListModels")
    .WithTags("OpenAI Compatible")
    .WithSummary("List models (OpenAI compatible)")
    .WithDescription("OpenAI-compatible endpoint for listing available TTS and/or ASR models, depending on SERVER_MODE")
    .Produces<object>(200);

if (mode.Tts)
{
app.MapPost("/v1/audio/speech", async (
    OpenAiSpeechRequest request,
    IModelCatalog modelCatalog,
    IModelCache modelCache,
    ITtsSynthesizer synthesizer,
    HttpContext httpContext,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    // logger.LogInformation("[API] Received /v1/audio/speech request: model={Model}, speed={Speed}, voice={Voice}, language={Language}, input_length={Length}",
    //     request.Model, request.Speed, request.Voice, request.Language ?? "null", request.Input?.Length ?? 0);
    
    if (string.IsNullOrWhiteSpace(request.Model) || !modelCatalog.TryGetModel(request.Model, out var model))
    {
        return Results.NotFound(new ErrorResponse("model_not_found", "The requested model was not found."));
    }

    if (string.IsNullOrWhiteSpace(request.Input))
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", "Input text is required."));
    }

    if (KokoroMetadata.IsKokoroEngine(model!.Engine))
    {
        if (!KokoroMetadata.TryNormalizeLanguage(request.Language, out var normalizedLanguage) ||
            !model.SupportedLanguages.Contains(normalizedLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ErrorResponse(
                "invalid_request",
                $"Unsupported language '{request.Language}'. Supported languages: {string.Join(", ", model.SupportedLanguages)}"));
        }

        var requestedSpeaker = request.Speaker;
        if (KokoroMetadata.IsDefaultVoiceValue(requestedSpeaker))
        {
            requestedSpeaker = request.Voice;
        }

        string? normalizedSpeaker = null;
        if (!KokoroMetadata.IsDefaultVoiceValue(requestedSpeaker))
        {
            if (!KokoroMetadata.TryNormalizeSpeaker(requestedSpeaker, out var speakerCandidate) ||
                !model.Speakers.Contains(speakerCandidate, StringComparer.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ErrorResponse(
                    "invalid_request",
                    $"Unsupported speaker '{requestedSpeaker}'. Supported speakers: {string.Join(", ", model.Speakers)}"));
            }

            normalizedSpeaker = speakerCandidate;
        }

        request = new OpenAiSpeechRequest
        {
            Model = request.Model,
            Input = request.Input,
            Voice = normalizedSpeaker ?? request.Voice,
            ResponseFormat = request.ResponseFormat,
            Speed = request.Speed,
            Language = normalizedLanguage,
            Speaker = normalizedSpeaker
        };
    }
    else if (SupertonicMetadata.IsSupertonic3Engine(model!.Engine))
    {
        // Validate language
        if (!SupertonicMetadata.TryNormalizeLanguage(request.Language, out var normalizedLanguage) ||
            !model.SupportedLanguages.Contains(normalizedLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ErrorResponse(
                "invalid_request",
                $"Unsupported language '{request.Language}'. Supported languages: {string.Join(", ", model.SupportedLanguages)}"));
        }

        // Validate speaker
        var requestedSpeaker = request.Speaker;
        if (SupertonicMetadata.IsDefaultVoiceValue(requestedSpeaker))
        {
            requestedSpeaker = request.Voice;
        }

        string? normalizedSpeaker = null;
        if (!SupertonicMetadata.IsDefaultVoiceValue(requestedSpeaker))
        {
            if (!SupertonicMetadata.TryNormalizeSpeaker(requestedSpeaker, out var speakerCandidate) ||
                !model.Speakers.Contains(speakerCandidate, StringComparer.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ErrorResponse(
                    "invalid_request",
                    $"Unsupported speaker '{requestedSpeaker}'. Supported speakers: {string.Join(", ", model.Speakers)}"));
            }

            normalizedSpeaker = speakerCandidate;
        }

        request = new OpenAiSpeechRequest
        {
            Model          = request.Model,
            Input          = request.Input,
            Voice          = normalizedSpeaker ?? request.Voice,
            ResponseFormat = request.ResponseFormat,
            Speed          = request.Speed,
            Language       = normalizedLanguage,
            Speaker        = normalizedSpeaker
        };
    }

    // Sanitize input text to prevent TTS engine crashes from special characters
    var sanitizedInput = TextSanitizer.Sanitize(request.Input, request.Language);
    
    if (string.IsNullOrWhiteSpace(sanitizedInput))
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", "Input text contains only unsupported characters."));
    }
    
    // Create sanitized request
    var sanitizedRequest = new OpenAiSpeechRequest
    {
        Model = request.Model,
        Input = sanitizedInput,
        Voice = request.Voice,
        ResponseFormat = request.ResponseFormat,
        Speed = request.Speed,
        Language = request.Language,
        Speaker = request.Speaker
    };

    // logger.LogInformation("[API] Speech synthesis request: model={Model}, speed={Speed}, voice={Voice}, language={Language}, input_length={Length}",
    //     request.Model, request.Speed, request.Voice, request.Language, request.Input.Length);

    var modelPath = await modelCache.EnsureModelAsync(model!, cancellationToken);
    var result = await synthesizer.SynthesizeAsync(model!, modelPath, sanitizedRequest, cancellationToken);

    // Add metrics to response headers
    httpContext.Response.Headers["X-Processing-Time"] = result.ProcessingTimeSeconds.ToString("F3");
    httpContext.Response.Headers["X-Chars-Per-Second"] = result.CharsPerSecond.ToString("F1");
    httpContext.Response.Headers["X-RTF"] = result.RTF.ToString("F3");
    httpContext.Response.Headers["X-Audio-Duration"] = result.AudioDurationSeconds.ToString("F2");
    httpContext.Response.Headers["X-Character-Count"] = result.CharacterCount.ToString();

    return Results.File(result.AudioBytes, result.ContentType, fileDownloadName: result.FileName);
})
    .WithName("CreateSpeech")
    .WithTags("OpenAI Compatible")
    .WithSummary("Generate speech from text")
    .WithDescription("Generates audio from the input text using the specified TTS model. OpenAI-compatible endpoint. Supports kokoro-q4 and kokoro-full with full Kokoro speaker catalog and languages: en-us, en-gb, es, fr-fr, hi, it, ja-jp, pt-br, zh-cn (plus accepted aliases). Also supports supertonic-3 with 31 languages and 10 preset voice styles (M1–M5, F1–F5).")
    .Accepts<OpenAiSpeechRequest>("application/json")
    .Produces<byte[]>(200, "audio/wav")
    .Produces<ErrorResponse>(400)
    .Produces<ErrorResponse>(404);
}

if (mode.Asr)
{
app.MapGet("/api/asr-models", (IAsrModelCatalog asrModelCatalog) =>
{
    var models = asrModelCatalog.GetSupportedModels().Select(m => new AsrModelDefinitionResponse(
        m.Name,
        m.DisplayName,
        m.Description,
        m.SupportedLanguages,
        m.SupportsLanguageAutoDetect,
        m.SupportsVad,
        m.SupportsStreaming));

    return Results.Ok(models);
})
    .WithName("GetAsrModels")
    .WithTags("Models")
    .WithSummary("List available ASR models")
    .WithDescription("Returns detailed information about all available speech-to-text models, including supported languages")
    .Produces<IEnumerable<AsrModelDefinitionResponse>>(200);

app.MapPost("/v1/audio/transcriptions", async (
    HttpRequest httpRequest,
    IAsrModelCatalog asrModelCatalog,
    IModelCache modelCache,
    IAsrTranscriber transcriber,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    if (!httpRequest.HasFormContentType)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", "Expected multipart/form-data with an audio file."));
    }

    var form = await httpRequest.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    var modelName = form["model"].ToString();
    var language = form["language"].ToString();
    var responseFormat = form["response_format"].ToString();
    var useVadRaw = form["use_vad"].ToString();
    var enableVad = string.Equals(useVadRaw, "true", StringComparison.OrdinalIgnoreCase) || useVadRaw == "1";
    var includeSegments = string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase);
    var minSegmentDurationRaw = form["min_segment_duration"].ToString();
    var minSegmentDuration = double.TryParse(minSegmentDurationRaw, out var parsedMinDuration) ? parsedMinDuration : 0.5;
    var vadOnsetRaw = form["vad_onset"].ToString();
    var vadOffsetRaw = form["vad_offset"].ToString();
    float? vadOnset = float.TryParse(vadOnsetRaw, out var parsedOnset) ? parsedOnset : null;
    float? vadOffset = float.TryParse(vadOffsetRaw, out var parsedOffset) ? parsedOffset : null;
    var punctuateRaw = form["punctuate"].ToString();
    var punctuate = string.Equals(punctuateRaw, "true", StringComparison.OrdinalIgnoreCase) || punctuateRaw == "1";
    var pncModel = string.IsNullOrWhiteSpace(form["pnc_model"].ToString()) ? null : form["pnc_model"].ToString();

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", "An audio file is required."));
    }

    if (string.IsNullOrWhiteSpace(modelName) || !asrModelCatalog.TryGetModel(modelName, out var model))
    {
        return Results.NotFound(new ErrorResponse("model_not_found", "The requested model was not found."));
    }

    // Read straight into an exact-size buffer: a MemoryStream here grew by reallocation and then
    // copied itself again with ToArray(), so every upload paid for two buffers.
    var audioBytes = new byte[file.Length];
    await using (var source = file.OpenReadStream())
    {
        var offset = 0;
        while (offset < audioBytes.Length)
        {
            var read = await source.ReadAsync(audioBytes.AsMemory(offset, audioBytes.Length - offset), cancellationToken);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset < audioBytes.Length)
        {
            audioBytes = audioBytes[..offset];
        }
    }

    // Normalize every upload (WAV, FLAC, MP3, OGG, WEBM, M4A, ...) to PCM16 mono WAV up front, so
    // neither engine needs to understand more than one input format.
    try
    {
        audioBytes = await AudioFormatConverter.ToPcm16WavAsync(audioBytes, cancellationToken);
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return Results.BadRequest(new ErrorResponse("invalid_request", $"Could not decode the uploaded audio file: {ex.Message}"));
    }

    var transcriptionRequest = new AudioTranscriptionRequest
    {
        Model = modelName,
        Language = string.IsNullOrWhiteSpace(language) ? null : language,
        ResponseFormat = string.IsNullOrWhiteSpace(responseFormat) ? "json" : responseFormat,
        EnableVad = enableVad,
        IncludeSegments = includeSegments,
        MinSegmentDurationSeconds = minSegmentDuration,
        VadOnset = vadOnset,
        VadOffset = vadOffset,
        Punctuate = punctuate,
        PncModel = pncModel
    };

    var modelDirectory = await modelCache.EnsureModelAsync(model!, cancellationToken);
    var result = await transcriber.TranscribeAsync(model!, modelDirectory, transcriptionRequest, audioBytes, cancellationToken);

    httpContext.Response.Headers["X-Processing-Time"] = result.ProcessingTimeSeconds.ToString("F3");
    httpContext.Response.Headers["X-Audio-Duration"] = result.AudioDurationSeconds.ToString("F2");
    httpContext.Response.Headers["X-RTF"] = result.Rtf.ToString("F3");
    httpContext.Response.Headers["X-Character-Count"] = result.CharacterCount.ToString();

    if (includeSegments)
    {
        return Results.Ok(new
        {
            text = result.Text,
            language = result.DetectedLanguage,
            duration = result.AudioDurationSeconds,
            segments = (result.Segments ?? []).Select(s => new { id = s.Id, start = s.Start, end = s.End, text = s.Text })
        });
    }

    return Results.Ok(new { text = result.Text });
})
    .WithName("CreateTranscription")
    .WithTags("OpenAI Compatible")
    .WithSummary("Transcribe audio to text")
    .WithDescription("Generates a transcription from the uploaded audio file using the specified ASR model. OpenAI-compatible endpoint. Multipart/form-data fields: file (required, any ffmpeg-decodable format - WAV, FLAC, MP3, OGG, WEBM, M4A, etc. - normalized server-side before transcription), model (required), language (optional), response_format (optional, `json` (default) or `verbose_json` - the latter adds a `segments` array of `{ id, start, end, text }` timestamped segments to the response), min_segment_duration (optional, seconds, default 0.5 - segments shorter than this are merged into a neighboring segment rather than dropped), use_vad (optional, `true`/`1` to enable voice-activity-detection - `nemotron-3.5` gates silent chunks and its segments reflect detected speech regions, `whisper-base` transcribes each VAD span separately; optional `vad_onset`/`vad_offset` override the configured hysteresis thresholds). Supports whisper-base (multilingual, auto language detection) and nemotron-3.5 (40 language-locales).")
    .Accepts<IFormFile>("multipart/form-data")
    .Produces<object>(200)
    .Produces<ErrorResponse>(400)
    .Produces<ErrorResponse>(404);

app.MapGet("/v1/audio/transcriptions/stream", async (
    HttpContext httpContext,
    IAsrModelCatalog asrModelCatalog,
    IModelCache modelCache,
    IAsrTranscriber transcriber,
    Microsoft.Extensions.Options.IOptions<AsrStreamingOptions> streamingOptions,
    Microsoft.Extensions.Options.IOptions<VadOptions> vadOptions,
    CancellationToken cancellationToken) =>
{
    if (!httpContext.WebSockets.IsWebSocketRequest)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsync("Expected a WebSocket upgrade request.", cancellationToken);
        return;
    }

    var modelName = httpContext.Request.Query["model"].ToString();
    var language = httpContext.Request.Query["language"].ToString();
    var useVadRaw = httpContext.Request.Query["use_vad"].ToString();
    var enableVad = string.Equals(useVadRaw, "true", StringComparison.OrdinalIgnoreCase) || useVadRaw == "1";
    var endpointRaw = httpContext.Request.Query["enable_endpointing"].ToString();
    // Request param overrides the configured default; endpointing needs VAD's per-chunk speech
    // decision, so it is only honoured when VAD is on.
    var enableEndpointing = string.Equals(endpointRaw, "true", StringComparison.OrdinalIgnoreCase) || endpointRaw == "1" ||
        (endpointRaw == "" && vadOptions.Value.EnableEndpointing);
    enableEndpointing &= enableVad;

    if (string.IsNullOrWhiteSpace(modelName) || !asrModelCatalog.TryGetModel(modelName, out var model))
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        await httpContext.Response.WriteAsync("The requested model was not found.", cancellationToken);
        return;
    }

    var streamingConfig = streamingOptions.Value;
    var segmentSeconds = streamingConfig.DefaultSegmentSeconds;
    if (double.TryParse(httpContext.Request.Query["segment_seconds"].ToString(), out var requestedSegmentSeconds))
    {
        segmentSeconds = Math.Clamp(requestedSegmentSeconds, streamingConfig.MinSegmentSeconds, streamingConfig.MaxSegmentSeconds);
    }

    // Works identically whether ASR runs in-process or in worker mode - IAsrTranscriber's
    // CreateStreamingSessionAsync hides a duplex gRPC call behind the same interface in worker mode.
    var resolvedLanguage = string.IsNullOrWhiteSpace(language) ? null : language;
    var modelDirectory = await modelCache.EnsureModelAsync(model!, cancellationToken);
    var session = await transcriber.CreateStreamingSessionAsync(model!, modelDirectory, resolvedLanguage, enableVad, enableEndpointing, segmentSeconds, cancellationToken);

    using var _ = session;
    using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
    await RunStreamingTranscriptionAsync(webSocket, session, cancellationToken);
})
    .WithName("StreamTranscription")
    .WithTags("OpenAI Compatible")
    .WithSummary("Stream live speech-to-text over WebSocket")
    .WithDescription("WebSocket upgrade endpoint for real-time transcription. Query params: model (required), " +
        "language (optional), use_vad (optional, nemotron-3.5 only), enable_endpointing (optional, " +
        "nemotron-3.5 with use_vad only - commits the current segment when trailing silence reaches " +
        "VadOptions.StopHistoryEouMs instead of waiting for the time/punctuation policy; defaults to " +
        "VadOptions.EnableEndpointing), segment_seconds (optional, default 10, " +
        "clamped to [5,30] - max seconds of audio before the current segment is force-committed; committed " +
        "sooner on sentence-ending punctuation). After upgrading, send binary WebSocket frames of raw 16-bit " +
        "PCM mono audio at 16kHz (no WAV/container header) - client-captured microphone or tab/system audio " +
        "must be resampled to this format before sending. The server replies with JSON text frames, each " +
        "scoped to the CURRENT segment only (never the whole conversation, so message size stays bounded " +
        "regardless of utterance length): `{\"type\":\"partial\",\"text\":\"...\"}` as the current segment's " +
        "text becomes available (replace the currently-displayed in-progress line), and " +
        "`{\"type\":\"segment\",\"text\":\"...\"}` once a segment is committed (append it to permanent " +
        "history and start a new in-progress line), with `\"endpoint_final\":true` when that commit came from " +
        "trailing silence rather than the time/punctuation policy. Nemotron uses true cache-aware incremental decoding; " +
        "Whisper has no incremental API and instead periodically re-transcribes its current segment's " +
        "buffered audio, which is trimmed down at each commit. Send a text frame `{\"type\":\"end\"}` (or " +
        "close the socket) to finish - the server replies once more with " +
        "`{\"type\":\"final\",\"text\":\"...\"}` (the trailing, not-yet-committed segment's text) before " +
        "closing. Works in both in-process and worker mode. Swagger UI cannot exercise WebSocket endpoints " +
        "via \"Try it out\"; this description documents the protocol only.");
}

app.MapFallbackToFile("/index.html");

app.Run();

static async Task RunStreamingTranscriptionAsync(WebSocket webSocket, IStreamingTranscriptionSession session, CancellationToken cancellationToken)
{
    const int InitialMessageBufferSize = 16 * 1024;

    // One pooled buffer for the life of the connection instead of a new MemoryStream per message:
    // streaming frames arrive continuously, so the per-message allocation was the hot path here.
    // The buffer only grows when a single message exceeds the current capacity. The session receives
    // a slice of it (see IStreamingTranscriptionSession: implementations copy what they need during
    // the call), so returning it to the pool once the awaited call completes is safe.
    var buffer = ArrayPool<byte>.Shared.Rent(InitialMessageBufferSize);
    var json = new ArrayBufferWriter<byte>();
    var writer = new Utf8JsonWriter(json);

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;

            do
            {
                if (length == buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                result = await webSocket.ReceiveAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                length += result.Count;
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var message = buffer.AsMemory(0, length);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(message.Span).Contains("\"end\"", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                continue;
            }

            var updates = await session.ProcessChunkAsync(message, cancellationToken);
            foreach (var update in updates)
            {
                await SendJsonAsync(webSocket, new { type = update.IsSegmentFinal ? "segment" : "partial", text = update.Text, endpoint_final = update.IsEndpointFinal }, json, writer, cancellationToken);
            }
        }

        var finalText = await session.FinishAsync(cancellationToken);
        await SendJsonAsync(webSocket, new { type = "final", text = finalText }, json, writer, cancellationToken);

        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
    }
    finally
    {
        writer.Dispose();
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

static async Task SendJsonAsync(WebSocket webSocket, object payload, ArrayBufferWriter<byte> json, Utf8JsonWriter writer, CancellationToken cancellationToken)
{
    // Both the buffer and the writer are reused for the whole connection, so steady-state sends
    // allocate nothing: Clear() resets the write position but keeps whatever capacity the buffer
    // already grew to, and Reset() reuses the writer itself rather than allocating a new one.
    json.Clear();
    writer.Reset(json);
    JsonSerializer.Serialize(writer, payload, payload.GetType());
    writer.Flush();
    await webSocket.SendAsync(json.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}

public partial class Program;
