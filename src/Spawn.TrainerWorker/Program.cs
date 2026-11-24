namespace Spawn.TrainerWorker;

/// <summary>
/// Isolated external trainer supervisor entrypoint (published under <c>Workers\Trainer\</c>).
/// Accepts <c>--host-pid &lt;pid&gt; --pipe &lt;name&gt; --nonce &lt;base64url&gt;</c> and runs windowless in the background.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (!TrainerWorkerApplication.TryParseArguments(args, out TrainerWorkerCliOptions? options) || options is null)
        {
            return 1;
        }

        var app = new TrainerWorkerApplication(options);
        return app.RunAsync().GetAwaiter().GetResult();
    }
}
