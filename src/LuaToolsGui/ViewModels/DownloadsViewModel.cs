using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The Downloads page. A thin projection over <see cref="DownloadQueue"/>: it holds no download state
/// of its own, so the queue stays the single source of truth for every entry point (Add, Fixes, the
/// store plugin and the protocol handler).
/// </summary>
public partial class DownloadsViewModel : ObservableObject
{
    private readonly DownloadQueue _queue;

    public DownloadsViewModel(DownloadQueue queue)
    {
        _queue = queue;

        _queue.Items.CollectionChanged += (_, _) => RaiseCounts();
        _queue.History.CollectionChanged += (_, _) => RaiseCounts();
        _queue.StateChanged += RaiseCounts;
    }

    /// <summary>Bound directly by the view: <c>Queue.Items</c> and <c>Queue.History</c>.</summary>
    public DownloadQueue Queue => _queue;

    /// <summary>Set by App: jump to the page that owns an item's pending confirmation.</summary>
    public Action<DownloadItem>? RevealItem { get; set; }

    public bool HasItems => _queue.Items.Count > 0;
    public bool HasHistory => _queue.History.Count > 0;
    public bool IsEmpty => !HasItems;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ── Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void Cancel(DownloadItem item) => _queue.Cancel(item);

    [RelayCommand]
    private void Retry(DownloadItem item) => _queue.Retry(item);

    /// <summary>Depot downloads only — see <see cref="DownloadItem.CanPause"/>.</summary>
    [RelayCommand]
    private void Pause(DownloadItem item) => _queue.Pause(item);

    [RelayCommand]
    private void Resume(DownloadItem item) => _queue.Resume(item);

    [RelayCommand]
    private void Remove(DownloadItem item) => _queue.Remove(item);

    [RelayCommand]
    private void MoveUp(DownloadItem item) => _queue.Move(item, -1);

    [RelayCommand]
    private void MoveDown(DownloadItem item) => _queue.Move(item, +1);

    [RelayCommand]
    private void ClearHistory() => _queue.ClearHistory();

    /// <summary>Jump to the page that can resolve this item (e.g. the pending overwrite confirmation).</summary>
    [RelayCommand]
    private void Review(DownloadItem item)
    {
        item.Job.OnReveal?.Invoke();
        RevealItem?.Invoke(item);
    }
}
