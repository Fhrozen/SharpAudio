// wiki: worker-core#worker-bootstrap
namespace SharpAudio.Core.Worker;

/// <summary>
/// Command-line arguments every task worker executable receives from the API. The <c>READY:{port}</c>
/// handshake on stdout is a byte-stable contract with
/// <c>SharpAudio.Core.Services.WorkerProcessManager</c>, so the argument order and names here are
/// part of that contract and must not change.
/// </summary>
public sealed record WorkerArguments
{
    public int Port { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Parse <c>--port</c>, <c>--model-key</c> and <c>--idle-timeout</c>, falling back to the
    /// task-specific defaults when an argument is absent.
    /// </summary>
    public static WorkerArguments Parse(string[] args, int defaultPort, int defaultIdleSeconds)
    {
        var arguments = new WorkerArguments
        {
            Port = defaultPort,
            IdleTimeout = TimeSpan.FromSeconds(defaultIdleSeconds)
        };

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    arguments = arguments with { Port = int.Parse(args[++i]) };
                    break;
                case "--model-key" when i + 1 < args.Length:
                    arguments = arguments with { ModelKey = args[++i] };
                    break;
                case "--idle-timeout" when i + 1 < args.Length:
                    arguments = arguments with { IdleTimeout = TimeSpan.FromSeconds(int.Parse(args[++i])) };
                    break;
            }
        }

        return arguments;
    }
}
