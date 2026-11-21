using System.Threading.Channels;
using Spawn.Contracts;

namespace Spawn.OverlayHost.Services;

/// <summary>
/// Serializes incoming <see cref="OverlayToastRequest"/> items into a smooth sequential playback queue
/// and automatically appends a celebratory PS5 Platinum Trophy card when the final achievement unlocks.
/// </summary>
public sealed class OverlayToastQueueCoordinator : IAsyncDisposable
{
    public const string SyntheticPlatinumApiName = "__SPAWN_PLATINUM_TROPHY__";

    private readonly Channel<OverlayToastRequest> _channel;
    private readonly Func<OverlayToastRequest, CancellationToken, Task> _presentSingleToastAsync;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private int _pendingCount;

    public int PendingToastCount => Volatile.Read(ref _pendingCount);

    public OverlayToastQueueCoordinator(Func<OverlayToastRequest, CancellationToken, Task> presentSingleToastAsync)
    {
        ArgumentNullException.ThrowIfNull(presentSingleToastAsync);
        _presentSingleToastAsync = presentSingleToastAsync;
        _channel = Channel.CreateBounded<OverlayToastRequest>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _consumerTask = Task.Run(() => ConsumeLoopAsync(_cts.Token), CancellationToken.None);
    }

    public void Enqueue(OverlayToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // If a normal achievement completes 100% under the PS5 Trophy theme, first show that achievement
        // as its normal Gold/Silver/Bronze trophy, then automatically chain the Platinum Trophy celebration!
        if (request.Theme == OverlayThemeKind.Ps5Trophy
            && request.IsPlatinumCompletion
            && !string.Equals(request.AchievementApiName, SyntheticPlatinumApiName, StringComparison.OrdinalIgnoreCase))
        {
            var primary = request with { IsPlatinumCompletion = false };
            WriteItem(primary);

            var platinumFollowUp = request with
            {
                AchievementApiName = SyntheticPlatinumApiName,
                DisplayName = "白金奖杯 · 全成就达成",
                Description = $"已集齐《{request.GameTitle}》全部成就！",
                IsRare = true,
                GlobalPercent = 0.1,
                IsPlatinumCompletion = true,
                Theme = OverlayThemeKind.Ps5Trophy,
                DurationMs = Math.Max(request.DurationMs, 5200),
            };
            WriteItem(platinumFollowUp);
            return;
        }

        WriteItem(request);
    }

    private void WriteItem(OverlayToastRequest item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _pendingCount);
        }
    }

    private async Task ConsumeLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var toast in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await _presentSingleToastAsync(toast, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Never allow a single malformed icon or animation fault to break subsequent queued toasts.
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                }

                // Brief breathing gap between consecutive multi-unlock toasts.
                if (Volatile.Read(ref _pendingCount) > 0 && !token.IsCancellationRequested)
                {
                    await Task.Delay(140, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown timeout.
        }

        _cts.Dispose();
    }
}
