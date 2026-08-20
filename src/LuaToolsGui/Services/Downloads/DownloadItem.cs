using CommunityToolkit.Mvvm.ComponentModel;

namespace LuaToolsGui.Services.Downloads;

public enum DownloadStatus
{
    Queued,
    Downloading,
    /// <summary>Downloaded, waiting on the user's overwrite confirmation. Holds no concurrency slot.</summary>
    AwaitingConfirmation,
    Installing,
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
    internal CancellationTokenSource Cts { get; }

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
        nameof(ShowProgress), nameof(NeedsAction), nameof(RateLabel), nameof(EtaLabel))]
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

    public bool IsIndeterminate => TotalBytes is not > 0;
    public double Percent => TotalBytes is > 0 ? BytesRead * 100d / TotalBytes.Value : 0;

    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading
        or DownloadStatus.AwaitingConfirmation or DownloadStatus.Installing;
    public bool IsRunning => Status is DownloadStatus.Downloading or DownloadStatus.Installing;

    public bool ShowProgress => Status is DownloadStatus.Downloading or DownloadStatus.Installing;
    public bool NeedsAction => Status is DownloadStatus.AwaitingConfirmation;
    public bool CanCancel => IsActive;
    public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Cancelled;
    public bool CanRemove => !IsActive;

    /// <summary>Reordering only means anything before the item starts; priority is its index in the queue.</summary>
    public bool CanReorder => Status is DownloadStatus.Queued;

    public string StatusLabel => Status switch
    {
        DownloadStatus.Queued => Resources.Strings.Downloads_Status_Queued,
        DownloadStatus.Downloading => Resources.Strings.Downloads_Status_Downloading,
        DownloadStatus.AwaitingConfirmation => Resources.Strings.Downloads_Status_AwaitingConfirm,
        DownloadStatus.Installing => Resources.Strings.Downloads_Status_Installing,
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
