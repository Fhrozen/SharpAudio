// wiki: backend-architecture#endpoints
namespace SharpAudio.Core.Contracts;

/// <summary>
/// Tells the frontend which task types this server instance was started with (SERVER_MODE).
/// </summary>
public sealed record ServerInfoResponse(
    bool TtsEnabled,
    bool AsrEnabled,
    bool MusicEnabled = false,
    bool DiarEnabled = false);
