using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// The app's single download scheduler: manifests and Denuvo fixes from every entry point (the Add
/// page, the Fixes page, the Steam store plugin and the protocol handler) run through this one queue.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> All queue state — <see cref="Items"/>, <see cref="History"/>, every
/// <see cref="DownloadItem"/> property and every scheduling decision — lives on the WPF dispatcher.
/// Only the HTTP stream and the install call run on background threads. That removes the need for any
/// locking around the collections and makes bindings safe by construction. The dispatcher is touched
/// once per state transition plus a throttled progress tick, never once per network chunk.</para>
///
/// <para><b>Why no SemaphoreSlim for the cap.</b> A semaphore can't be resized at runtime (the user can
/// change the setting mid-queue) and can't express "start the first <i>Queued</i> item in list order",
/// which is what makes reordering work. Instead the pump re-evaluates the list on every
/// <see cref="Kick"/>, and priority is simply the item's index in <see cref="Items"/>.</para>
///
/// <para><b>Installs are always serialized</b> behind <c>_installGate</c>, independent of the download
/// cap: <c>LuaInstaller.InstallZip</c> writes into Steam's shared depotcache with a File.Exists guard
/// that two concurrent installs would race.</para>
/// </remarks>
public class DownloadQueue : IHostedService
{
    private readonly SettingsService _settings;
    private readonly CacheService _cache;
    private readonly ILogger<DownloadQueue> _log;

    /// <summary>Signals the pump that the schedule may have changed.</summary>
    private readonly SemaphoreSlim _kick = new(0);

    /// <summary>Serializes the install phase. See the remarks above.</summary>
    private readonly SemaphoreSlim _installGate = new(1, 1);

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;
    private int _running;

    public DownloadQueue(SettingsService settings, CacheService cache, ILogger<DownloadQueue> log)
    {
        _settings = settings;
        _cache = cache;
        _log = log;
        _maxConcurrent = settings.MaxConcurrentDownloads;
    }

    private Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>Active and recently finished items, in scheduling order. Index = priority.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = [];

    /// <summary>Finished downloads from this and previous sessions, newest first.</summary>
    public ObservableCollection<DownloadHistoryEntry> History { get; } = [];

    /// <summary>Raised when <see cref="ActiveCount"/> or <see cref="MaxConcurrent"/> changes.</summary>
    public event Action? StateChanged;

    private int _maxConcurrent;

    /// <summary>How many downloads may run at once (1..5). Lowering this never cancels a running item.</summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            int v = Math.Clamp(value, 1, 5);
            if (v == _maxConcurrent) return;
            _maxConcurrent = v;
            _settings.MaxConcurrentDownloads = v;
            StateChanged?.Invoke();
            Kick();
        }
    }

    public int ActiveCount => Items.Count(i => i.IsActive);

    // ── Lifecycle ────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var r in _cache.GetDownloadHistory().OrderByDescending(r => r.CompletedAtMs))
            History.Add(new DownloadHistoryEntry(r));

        _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancel everything in flight and record it. Anything still active when the app closes is written
    /// to history as cancelled rather than silently vanishing.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _shutdown.Cancel();

        List<DownloadItem> active = [];
        await Dispatcher.InvokeAsync(() => active = Items.Where(i => i.IsActive).ToList());

        foreach (var item in active)
        {
            try { item.Cts.Cancel(); } catch { /* already disposed */ }
        }

        await Dispatcher.InvokeAsync(() =>
        {
            foreach (var item in active)
            {
                if (!item.IsActive) continue;
                item.Message ??= Resources.Strings.Downloads_Err_Interrupted;
                item.Status = DownloadStatus.Cancelled;
                History.Insert(0, new DownloadHistoryEntry(
                    DownloadHistoryEntry.From(item, DownloadStatus.Cancelled)));
                item.SettleCompletion(null);
            }
            PersistHistory();
        });

        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch { /* pump is parked on _kick; shutdown proceeds regardless */ }
        }
    }

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Add a job, or return the existing active item with the same
    /// <see cref="DownloadJob.DedupeKey"/>. This is the app-wide replacement for the per-page
    /// <c>if (IsBusy) return;</c> gates and for the HTTP server's duplicate-appid 409 check.
    /// </summary>
    public DownloadItem Enqueue(DownloadJob job)
    {
        return Dispatcher.Invoke(() =>
        {
            if (FindActiveCore(job.DedupeKey) is { } existing)
            {
                _log.LogDebug("Download already queued, reusing item: {Key}", job.DedupeKey);
                return existing;
            }

            var item = new DownloadItem(job);
            Items.Add(item);
            item.PropertyChanged += OnItemPropertyChanged;
            StateChanged?.Invoke();
            Kick();
            return item;
        });
    }

    /// <summary>The in-flight item for a dedupe key, or null.</summary>
    public DownloadItem? FindActive(string dedupeKey) =>
        Dispatcher.Invoke(() => FindActiveCore(dedupeKey));

    private DownloadItem? FindActiveCore(string dedupeKey) =>
        Items.FirstOrDefault(i => i.IsActive &&
            string.Equals(i.Job.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase));

    public void Cancel(DownloadItem item) => Dispatcher.Invoke(() =>
    {
        if (!item.IsActive) return;
        // A queued item never entered RunItemAsync, so nothing will observe the token. Finish it here.
        bool wasQueued = item.Status == DownloadStatus.Queued;
        try { item.Cts.Cancel(); } catch { }
        if (wasQueued) Finish(item, DownloadStatus.Cancelled, Resources.Strings.Err_CancelledByUser, null);
        Kick();
    });

    /// <summary>Re-enqueue a failed or cancelled item's job as a fresh item at the tail.</summary>
    public DownloadItem Retry(DownloadItem item)
    {
        Dispatcher.Invoke(() => Remove(item));
        return Enqueue(item.Job);
    }

    /// <summary>Move a pending item up (-1) or down (+1). No-op once it has started.</summary>
    public void Move(DownloadItem item, int delta) => Dispatcher.Invoke(() =>
    {
        if (item.Status != DownloadStatus.Queued) return;
        int from = Items.IndexOf(item);
        if (from < 0) return;
        int to = Math.Clamp(from + delta, 0, Items.Count - 1);
        if (to == from) return;
        Items.Move(from, to);
        Kick();
    });

    /// <summary>Drop a finished item from the active list. History keeps the record.</summary>
    public void Remove(DownloadItem item) => Dispatcher.Invoke(() =>
    {
        if (item.IsActive) return;
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
        StateChanged?.Invoke();
    });

    public void ClearHistory() => Dispatcher.Invoke(() =>
    {
        History.Clear();
        PersistHistory();
    });

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status)) StateChanged?.Invoke();
    }

    // ── Scheduler ────────────────────────────────────────────────────

    private void Kick()
    {
        try { _kick.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _kick.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                await Dispatcher.InvokeAsync(StartEligible);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Download pump cycle failed");
            }
        }
    }

    /// <summary>Start as many queued items as the cap allows, in list order. Dispatcher thread only.</summary>
    private void StartEligible()
    {
        while (_running < _maxConcurrent)
        {
            var next = Items.FirstOrDefault(i => i.Status == DownloadStatus.Queued
                                                 && !i.Cts.IsCancellationRequested);
            if (next is null) return;

            _running++;
            next.Status = DownloadStatus.Downloading;
            StateChanged?.Invoke();
            _ = RunItemAsync(next);
        }
    }

    private async Task RunItemAsync(DownloadItem item)
    {
        bool holdsSlot = true;
        DownloadedFile? file = null;
        var ct = item.Cts.Token;

        try
        {
            // ── 1. Download ──────────────────────────────────────────
            // Progress is time-throttled here rather than in the services: a report arrives per 80 KB
            // chunk (~25,000 for a 2 GB zip) and posting each one would flood the UI thread.
            long lastPostTicks = 0;
            var sink = new ProgressRelay<DownloadProgress>(p =>
            {
                long now = DateTime.UtcNow.Ticks;
                bool done = p.TotalBytes is > 0 && p.BytesRead >= p.TotalBytes.Value;
                if (!done && now - lastPostTicks < TimeSpan.TicksPerMillisecond * 100) return;
                lastPostTicks = now;
                _ = Dispatcher.InvokeAsync(() => item.ApplySample(p.BytesRead, p.TotalBytes),
                    DispatcherPriority.Background);
            });

            file = await Task.Run(() => item.Job.DownloadAsync(sink, ct), ct);

            // ── 2. Optional confirmation gate ────────────────────────
            if (item.Job.ConfirmAsync is { } confirm)
            {
                // Release the slot BEFORE awaiting. A dialog the user never answers must not wedge the
                // queue — at the default concurrency of 1 that would block every later download forever.
                await Dispatcher.InvokeAsync(() =>
                {
                    item.Status = DownloadStatus.AwaitingConfirmation;
                    _running--;
                    StateChanged?.Invoke();
                });
                holdsSlot = false;
                Kick();

                bool proceed;
                try { proceed = await confirm(file, item, ct); }
                catch (OperationCanceledException) { proceed = false; }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Download confirm gate threw; treating as declined");
                    proceed = false;
                }

                if (!proceed || ct.IsCancellationRequested)
                {
                    DeleteStaged(file.FilePath);
                    await Dispatcher.InvokeAsync(() =>
                        Finish(item, DownloadStatus.Cancelled, Resources.Strings.Add_Status_Cancelled, null));
                    return;
                }

                // Re-take a slot for the install phase. Installs are gated separately anyway, so this
                // is just bookkeeping to keep ActiveCount honest.
                await Dispatcher.InvokeAsync(() => { _running++; StateChanged?.Invoke(); });
                holdsSlot = true;
            }

            // ── 3. Install (always serialized) ───────────────────────
            await Dispatcher.InvokeAsync(() => item.Status = DownloadStatus.Installing);

            await _installGate.WaitAsync(CancellationToken.None);
            JobResult result;
            try
            {
                result = await Task.Run(() => item.Job.InstallAsync(file!, item, ct), CancellationToken.None);
            }
            finally { _installGate.Release(); }

            await Dispatcher.InvokeAsync(() => Finish(
                item,
                result.Ok ? DownloadStatus.Completed : DownloadStatus.Failed,
                result.Message,
                result));
        }
        catch (OperationCanceledException)
        {
            if (file is not null) DeleteStaged(file.FilePath);
            await Dispatcher.InvokeAsync(() =>
                Finish(item, DownloadStatus.Cancelled, Resources.Strings.Err_CancelledByUser, null));
        }
        catch (Exception ex)
        {
            if (file is not null) DeleteStaged(file.FilePath);
            _log.LogDebug(ex, "Download job failed: {Key}", item.Job.DedupeKey);
            // Both of these carry a message meant for the user; anything else is unexpected, so it gets
            // the generic text and the detail goes to the log above.
            string message = ex is ApiException or DownloadAbortedException
                ? ex.Message
                : Resources.Strings.Add_Err_Download;
            await Dispatcher.InvokeAsync(() => Finish(item, DownloadStatus.Failed, message, null));
        }
        finally
        {
            if (holdsSlot)
                await Dispatcher.InvokeAsync(() => { _running--; StateChanged?.Invoke(); });
            Kick();
        }
    }

    /// <summary>Settle an item into a terminal state and record it. Dispatcher thread only.</summary>
    private void Finish(DownloadItem item, DownloadStatus status, string? message, JobResult? result)
    {
        if (!item.IsActive) return; // already settled (e.g. cancelled while queued)

        item.Message = message;
        item.Status = status;

        History.Insert(0, new DownloadHistoryEntry(DownloadHistoryEntry.From(item, status)));
        while (History.Count > 100) History.RemoveAt(History.Count - 1);
        PersistHistory();

        item.SettleCompletion(result);

        // Per-job continuation only. There is deliberately no queue-wide "finished" event: each entry
        // point already reports its own outcome, and a global subscriber double-notified all of them.
        try { item.Job.OnFinished?.Invoke(item, result); }
        catch (Exception ex) { _log.LogDebug(ex, "Download OnFinished continuation threw"); }

        StateChanged?.Invoke();
    }

    private void PersistHistory()
    {
        try { _cache.SaveDownloadHistory(History.Select(h => h.Record)); }
        catch (Exception ex) { _log.LogDebug(ex, "Persisting download history failed"); }
    }

    /// <summary>Best-effort delete of a staged file the install never consumed.</summary>
    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
