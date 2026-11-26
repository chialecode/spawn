using Spawn.Contracts;

namespace Spawn.OverlayHost.Services;

/// <summary>
/// Serializes incoming <see cref="OverlayToastRequest"/> items into a smooth sequential playback queue with:
/// - Automatic PS5 Platinum Trophy follow-up chaining on 100% completion;
/// - Congestion governance: shortens single-toast display duration when backlog &gt; 3 and collapses
///   queued bursts of &gt;= 8 unlocks into a single summary toast ("已连续解锁 N 项成就");
/// - Immediate preview replacement (<see cref="OverlayToastRequest.ReplaceActivePreview"/>) that cancels
///   any currently displayed preview toast without waiting for its hold timer;
/// - Emergency <see cref="ClearQueue"/> support for <c>overlay.clearQueue</c> IPC.
/// </summary>
public sealed class OverlayToastQueueCoordinator : IAsyncDisposable
{
    public const string SyntheticPlatinumApiName = "__SPAWN_PLATINUM_TROPHY__";
    public const string SyntheticBatchSummaryApiName = "__SPAWN_BATCH_SUMMARY__";
    public const int AccelerateBacklogThreshold = 3;
    public const int AcceleratedDisplayDurationMs = 2200;
    public const int CollapseBacklogThreshold = 8;
    private const int MaxQueueCapacity = 256;

    private readonly Lock _queueLock = new();
    private readonly LinkedList<OverlayToastRequest> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Func<OverlayToastRequest, CancellationToken, Task> _presentSingleToastAsync;
    private readonly int _interToastDelayMs;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _consumerTask;

    private CancellationTokenSource? _activeToastCts;
    private bool _isPresenting;
    private bool _activeToastIsPreview;
    private bool _disposed;

    public int PendingToastCount
    {
        get
        {
            lock (_queueLock)
            {
                return _queue.Count + (_isPresenting ? 1 : 0);
            }
        }
    }

    public OverlayToastQueueCoordinator(
        Func<OverlayToastRequest, CancellationToken, Task> presentSingleToastAsync,
        int interToastDelayMs = 140)
    {
        ArgumentNullException.ThrowIfNull(presentSingleToastAsync);
        _presentSingleToastAsync = presentSingleToastAsync;
        _interToastDelayMs = Math.Max(0, interToastDelayMs);
        _consumerTask = Task.Run(() => ConsumeLoopAsync(_disposeCts.Token), CancellationToken.None);
    }

    public void Enqueue(OverlayToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnqueueBatch([request]);
    }

    public void EnqueueBatch(IReadOnlyList<OverlayToastRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return;
        }

        int addedSignals = 0;
        lock (_queueLock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var request in requests)
            {
                if (request is null)
                {
                    continue;
                }

                if (request.ReplaceActivePreview)
                {
                    // Drop any stale queued previews and place the latest preview at the front of the queue.
                    RemoveQueuedPreviewsLocked();
                    _queue.AddFirst(request);
                    addedSignals++;

                    if (_isPresenting && _activeToastIsPreview)
                    {
                        CancelActiveToastLocked();
                    }

                    continue;
                }

                // If a normal achievement completes 100% under the PS5 Trophy theme, first show that achievement
                // as its normal Gold/Silver/Bronze trophy, then automatically chain the Platinum Trophy celebration!
                if (request.Theme == OverlayThemeKind.Ps5Trophy
                    && request.IsPlatinumCompletion
                    && !string.Equals(request.AchievementApiName, SyntheticPlatinumApiName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(request.AchievementApiName, SyntheticBatchSummaryApiName, StringComparison.OrdinalIgnoreCase))
                {
                    var primary = request with { IsPlatinumCompletion = false };
                    EnqueueBoundedLocked(primary);
                    addedSignals++;

                    var platinumFollowUp = request with
                    {
                        AchievementApiName = SyntheticPlatinumApiName,
                        DisplayName = "白金奖杯 · 全成就达成",
                        Description = $"已集齐《{request.GameTitle}》全部成就！",
                        IsRare = true,
                        GlobalPercent = 0.1,
                        IsPlatinumCompletion = true,
                        Theme = OverlayThemeKind.Ps5Trophy,
                        DurationMs = Math.Max(request.DisplayDurationMs ?? request.DurationMs, 5200),
                        DisplayDurationMs = Math.Max(request.DisplayDurationMs ?? request.DurationMs, 5200),
                    };
                    EnqueueBoundedLocked(platinumFollowUp);
                    addedSignals++;
                    continue;
                }

                EnqueueBoundedLocked(request);
                addedSignals++;
            }
        }

        if (addedSignals > 0)
        {
            _signal.Release(addedSignals);
        }
    }

    /// <summary>
    /// Emergency queue purge (invoked by <c>overlay.clearQueue</c> IPC): drops all pending queued toasts
    /// and immediately aborts whichever toast is currently on screen.
    /// </summary>
    public int ClearQueue()
    {
        lock (_queueLock)
        {
            int cleared = _queue.Count + (_isPresenting ? 1 : 0);
            _queue.Clear();
            CancelActiveToastLocked();
            return cleared;
        }
    }

    private void EnqueueBoundedLocked(OverlayToastRequest item)
    {
        while (_queue.Count >= MaxQueueCapacity)
        {
            _queue.RemoveFirst();
        }

        _queue.AddLast(item);
    }

    private void RemoveQueuedPreviewsLocked()
    {
        var node = _queue.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.ReplaceActivePreview)
            {
                _queue.Remove(node);
            }

            node = next;
        }
    }

    private void CancelActiveToastLocked()
    {
        try
        {
            _activeToastCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Active toast already completed.
        }
    }

    private bool TryDequeueNextToast(
        out OverlayToastRequest? nextToast,
        out CancellationTokenSource? activeCts)
    {
        lock (_queueLock)
        {
            if (_queue.Count == 0 || _disposed)
            {
                nextToast = null;
                activeCts = null;
                return false;
            }

            OverlayToastRequest first = _queue.First!.Value;
            if (first.ReplaceActivePreview)
            {
                _queue.RemoveFirst();
                nextToast = first;
            }
            else
            {
                int nonPreviewCount = 0;
                for (var node = _queue.First; node is not null; node = node.Next)
                {
                    if (!node.Value.ReplaceActivePreview)
                    {
                        nonPreviewCount++;
                    }
                }

                if (nonPreviewCount >= CollapseBacklogThreshold)
                {
                    var collapsed = new List<OverlayToastRequest>(nonPreviewCount);
                    var node = _queue.First;
                    while (node is not null)
                    {
                        var nextNode = node.Next;
                        if (!node.Value.ReplaceActivePreview)
                        {
                            collapsed.Add(node.Value);
                            _queue.Remove(node);
                        }

                        node = nextNode;
                    }

                    nextToast = BuildCollapsedSummaryToast(collapsed);
                }
                else
                {
                    int queueSizeBeforePop = nonPreviewCount;
                    _queue.RemoveFirst();
                    nextToast = first;

                    if (queueSizeBeforePop > AccelerateBacklogThreshold)
                    {
                        int baseDuration = nextToast.DisplayDurationMs ?? nextToast.DurationMs;
                        int shortened = Math.Min(baseDuration, AcceleratedDisplayDurationMs);
                        nextToast = nextToast with
                        {
                            DurationMs = shortened,
                            DisplayDurationMs = shortened,
                        };
                    }
                }
            }

            activeCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _activeToastCts = activeCts;
            _isPresenting = true;
            _activeToastIsPreview = nextToast.ReplaceActivePreview;
            return true;
        }
    }

    internal static OverlayToastRequest BuildCollapsedSummaryToast(IReadOnlyList<OverlayToastRequest> collapsed)
    {
        OverlayToastRequest representative = collapsed[^1];
        bool allSameGame = collapsed.All(t => string.Equals(t.GameTitle, representative.GameTitle, StringComparison.OrdinalIgnoreCase));
        string effectiveGameTitle = allSameGame && !string.IsNullOrWhiteSpace(representative.GameTitle)
            ? representative.GameTitle
            : "SPAWN 成就中心";

        var sampleNames = collapsed
            .Select(static t => t.DisplayName)
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        string description = sampleNames.Count switch
        {
            0 => "可在成就中心查看全部已解锁成就详情",
            1 => $"含「{sampleNames[0]}」等，可在成就中心查看全部详情",
            _ => $"含「{sampleNames[0]}」「{sampleNames[1]}」等，可在成就中心查看全部详情",
        };

        bool anyRare = collapsed.Any(static t => t.IsRare || t.GlobalPercent is < 10.0);
        bool anyPlatinum = collapsed.Any(static t => t.IsPlatinumCompletion);
        double? rarestPercent = collapsed
            .Where(static t => t.GlobalPercent.HasValue)
            .Select(static t => t.GlobalPercent!.Value)
            .DefaultIfEmpty()
            .Min();
        if (!collapsed.Any(static t => t.GlobalPercent.HasValue))
        {
            rarestPercent = null;
        }

        int summaryDurationMs = Math.Max(AcceleratedDisplayDurationMs, 3800);
        return representative with
        {
            RequestId = Guid.NewGuid().ToString("N"),
            GameTitle = effectiveGameTitle,
            AchievementApiName = SyntheticBatchSummaryApiName,
            DisplayName = $"已连续解锁 {collapsed.Count} 项成就",
            Description = description,
            IsRare = anyRare,
            GlobalPercent = rarestPercent,
            IsPlatinumCompletion = anyPlatinum,
            ReplaceActivePreview = false,
            DurationMs = summaryDurationMs,
            DisplayDurationMs = summaryDurationMs,
        };
    }

    private async Task ConsumeLoopAsync(CancellationToken disposeToken)
    {
        try
        {
            while (!disposeToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(disposeToken).ConfigureAwait(false);

                while (!disposeToken.IsCancellationRequested)
                {
                    if (!TryDequeueNextToast(out var toast, out var activeCts) || toast is null || activeCts is null)
                    {
                        break;
                    }

                    bool abortedByReplacementOrClear = false;
                    try
                    {
                        await _presentSingleToastAsync(toast, activeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (disposeToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        // Active toast was intentionally cancelled by ReplaceActivePreview or ClearQueue.
                        abortedByReplacementOrClear = true;
                    }
                    catch
                    {
                        // Never allow a single malformed icon or animation fault to break subsequent queued toasts.
                    }
                    finally
                    {
                        lock (_queueLock)
                        {
                            if (ReferenceEquals(_activeToastCts, activeCts))
                            {
                                _activeToastCts = null;
                            }

                            _isPresenting = false;
                            _activeToastIsPreview = false;
                        }

                        activeCts.Dispose();
                    }

                    bool hasMore;
                    bool nextIsPreview;
                    lock (_queueLock)
                    {
                        hasMore = _queue.Count > 0;
                        nextIsPreview = hasMore && _queue.First!.Value.ReplaceActivePreview;
                    }

                    // Brief breathing gap between consecutive multi-unlock toasts (skipped for immediate preview replacement).
                    if (hasMore && !abortedByReplacementOrClear && !nextIsPreview && _interToastDelayMs > 0 && !disposeToken.IsCancellationRequested)
                    {
                        await Task.Delay(_interToastDelayMs, disposeToken).ConfigureAwait(false);
                    }
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
        lock (_queueLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.Clear();
            CancelActiveToastLocked();
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown timeout.
        }

        _disposeCts.Dispose();
        _signal.Dispose();
    }
}
