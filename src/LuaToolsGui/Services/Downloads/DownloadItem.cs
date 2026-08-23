using CommunityToolkit.Mvvm.ComponentModel;

namespace LuaToolsGui.Services.Downloads;

public enum DownloadStatus
{
    Queued,
    Downloading,
    /// <summary>Downloaded, waiting on the user's overwrite confirmation. Holds no concurrency slot.</summary>
    AwaitingConfirmation,
    Installing,
    /// <summary>Depot download only: the user paused it. Not terminal - Resume picks it back up.</summary>
    Paused,
    /// <summary>Depot download only: re-hashing on-disk chunks after a resume, before bytes move again.</summary>
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// A live row in the download queue: the job, its current phase, and its byte/speed/ETA metrics.
/// </summary>
/// <remarks>
/// Every mutable property is written on the WPF dispatcher by <see cref="DownloadQueue"/>, so bindings
/// never need to marshal. Speed and ETA are derived here rather than in the download services, because
/// the services only ever see one chunk at a time and have no notion of a sampling window.
/// </remarks>
public partial class DownloadItem : ObservableObject
{
    private readonly TaskCompletionSource<JobResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Sliding-window rate samples: (timestamp ticks, bytes read). Small ring, oldest trimmed by age.
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);
    private const int MaxSamples = 20;

    public DownloadItem(DownloadJob job)
    {
        Job = job;
        Cts = new CancellationTokenSource();
        EnqueuedAt = DateTimeOffset.Now;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DownloadJob Job { get; }
    public DateTimeOffset EnqueuedAt { get; }
    internal CancellationTokenSource Cts { get; private set; }

    /// <summary>
    /// Set by <c>DownloadQueue.Pause</c> before it cancels the token, so the run loop can tell a pause
    /// from a real cancellation — both surface as an OperationCanceledException, but only one of them
    /// should settle the item.
    /// </summary>
    internal bool PauseRequested { get; set; }

    /// <summary>
    /// Set by Resume. Makes the next depot run pass -validate, which is MANDATORY on a resume: without
    /// it the downloader short-circuits and reports success over a half-written, pre-allocated file.
    /// Cleared once consumed, so only the interrupted depot pays the re-hash cost.
    /// </summary>
    internal bool NeedsValidate { get; set; }

    /// <summary>Swap in a fresh token source so a paused item can run again.</summary>
    internal void ResetCts()
    {
        try { Cts.Dispose(); } catch { /* already disposed */ }
        Cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Completes when the item reaches a terminal state, with the install result (null if it never got
    /// that far). NEVER faults. Inspect <see cref="Status"/> to distinguish failure from cancellation.
    /// </summary>
    /// <remarks>
    /// This is what the protocol/silent-install path and <c>PluginAddService</c> await in place of the
    /// old inline download→install chain. Without it, backgrounding the download would let
    /// <c>App</c>'s post-silent-install shutdown timer fire while the download was still running.
    /// </remarks>
    public Task<JobResult?> Completion => _completion.Task;

    public string Title => Job.Title;
    public string SubTitle => Job.SubTitle;
    public string? CoverPath => Job.CoverPath;
    public long AppId => Job.AppId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning), nameof(StatusLabel),
        nameof(CanCancel), nameof(CanRetry), nameof(CanRemove), nameof(CanReorder),
        nameof(ShowProgress), nameof(NeedsAction), nameof(RateLabel), nameof(EtaLabel),
        nameof(CanPause), nameof(CanResume))]
    private DownloadStatus _status = DownloadStatus.Queued;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(SizeLabel))]
    private long _bytesRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(SizeLabel), nameof(IsIndeterminate))]
    private long? _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateLabel))]
    private double _bytesPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EtaLabel))]
    private TimeSpan? _eta;

    /// <summary>Error text, or the install status line once finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>Extra sub-line while running, e.g. a depot job's "Depots - 3 of 12".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>
    /// Depot ids this job already finished. Resume skips them outright rather than re-hashing tens of GB.
    /// In-memory only: a DownloadJob holds delegates and is not serializable, so a paused item does not
    /// survive an app restart (same as every other in-flight item).
    /// </summary>
    internal HashSet<long> CompletedDepots { get; } = [];

    public bool IsIndeterminate => TotalBytes is not > 0;
    public double Percent => TotalBytes is > 0 ? BytesRead * 100d / TotalBytes.Value : 0;

    // Paused/Verifying are non-terminal, so they count as active: the item keeps its Cancel button,
    // is never auto-dismissed, and is recorded as cancelled if the app shuts down while it sits there.
    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading
        or DownloadStatus.AwaitingConfirmation or DownloadStatus.Installing
        or DownloadStatus.Paused or DownloadStatus.Verifying;
    public bool IsRunning => Status is DownloadStatus.Downloading or DownloadStatus.Installing
        or DownloadStatus.Verifying;

    public bool ShowProgress => Status is DownloadStatus.Downloading or DownloadStatus.Installing
        or DownloadStatus.Paused or DownloadStatus.Verifying;
    public bool NeedsAction => Status is DownloadStatus.AwaitingConfirmation;
    public bool CanCancel => IsActive;
    public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Cancelled;
    public bool CanRemove => !IsActive;

    /// <summary>Reordering only means anything before the item starts; priority is its index in the queue.</summary>
    public bool CanReorder => Status is DownloadStatus.Queued;

    // Pause/Resume exist only for depot downloads: they're the only job kind whose progress survives the
    // process being killed, because the bytes are already on disk and the tool can re-validate them.
    // Verifying counts too: re-hashing a large depot can run for minutes, and losing the Pause button
    // for exactly that stretch is when you'd most want it. Pausing a verify is safe — validation only
    // reads, and Resume re-validates from scratch anyway.
    public bool CanPause => Status is (DownloadStatus.Downloading or DownloadStatus.Verifying)
        && Job.Kind is DownloadKind.Depot;
    public bool CanResume => Status is DownloadStatus.Paused;

    public string StatusLabel => Status switch
    {
        DownloadStatus.Queued => Resources.Strings.Downloads_Status_Queued,
        DownloadStatus.Downloading => Resources.Strings.Downloads_Status_Downloading,
        DownloadStatus.AwaitingConfirmation => Resources.Strings.Downloads_Status_AwaitingConfirm,
        DownloadStatus.Installing => Resources.Strings.Downloads_Status_Installing,
        DownloadStatus.Paused => Resources.Strings.Downloads_Status_Paused,
        DownloadStatus.Verifying => Resources.Strings.Downloads_Status_Verifying,
        DownloadStatus.Completed => Resources.Strings.Downloads_Status_Completed,
        DownloadStatus.Failed => Resources.Strings.Downloads_Status_Failed,
        _ => Resources.Strings.Downloads_Status_Cancelled,
    };

    /// <summary>"412 MB of 1.4 GB", or just "412 MB" when the total length is unknown.</summary>
    public string SizeLabel
    {
        get
        {
            if (BytesRead <= 0 && TotalBytes is not > 0) return "";
            if (TotalBytes is > 0)
                return string.Format(Resources.Strings.Downloads_Of,
                    ByteFormat.Size(BytesRead), ByteFormat.Size(TotalBytes.Value));
            return ByteFormat.Size(BytesRead);
        }
    }

    public string RateLabel => Status is DownloadStatus.Downloading ? ByteFormat.Rate(BytesPerSecond) : "";

    public string EtaLabel
    {
        get
        {
            if (Status is not DownloadStatus.Downloading || Eta is not { } eta) return "";
            string d = ByteFormat.Duration(eta);
            return d.Length == 0 ? "" : string.Format(Resources.Strings.Downloads_Eta, d);
        }
    }

    /// <summary>
    /// Fold one progress reading in and recompute rate/ETA. Called on the dispatcher from the queue's
    /// throttled pump, NOT once per network chunk.
    /// </summary>
    internal void ApplySample(long bytesRead, long? total)
    {
        BytesRead = bytesRead;
        TotalBytes = total;

        long now = DateTime.UtcNow.Ticks;
        _samples.Enqueue((now, bytesRead));
        while (_samples.Count > MaxSamples ||
               (_samples.Count > 1 && now - _samples.Peek().Ticks > RateWindow.Ticks))
            _samples.Dequeue();

        if (_samples.Count < 2) return;

        var (oldTicks, oldBytes) = _samples.Peek();
        double seconds = (now - oldTicks) / (double)TimeSpan.TicksPerSecond;
        if (seconds <= 0) return;

        double rate = (bytesRead - oldBytes) / seconds;
        BytesPerSecond = rate;
        Eta = rate > 0 && total is > 0 && total.Value > bytesRead
            ? TimeSpan.FromSeconds((total.Value - bytesRead) / rate)
            : null;
    }

    /// <summary>Clear the rate window so a retry doesn't inherit the previous attempt's samples.</summary>
    internal void ResetMetrics()
    {
        _samples.Clear();
        BytesRead = 0;
        TotalBytes = null;
        BytesPerSecond = 0;
        Eta = null;
        Message = null;
    }

    /// <summary>Settle <see cref="Completion"/>. Idempotent: a retry re-enqueues a NEW item.</summary>
    internal void SettleCompletion(JobResult? result) => _completion.TrySetResult(result);
}
