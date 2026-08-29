using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>A game card in the Achievements grid.</summary>
public partial class AchievementGameCardVm(long appId, string name, int achievementCount, bool isInstalled)
    : ObservableObject
{
    public long AppId { get; } = appId;
    public string AppIdText { get; } = appId.ToString();
    public int AchievementCount { get; } = achievementCount;
    public bool IsInstalled { get; } = isInstalled;

    [ObservableProperty] private string _name = name;

    /// <summary>Local cached cover path (bound via ImagePathToSource), resolved when the page is shown.</summary>
    [ObservableProperty] private string? _cover;

    private int _resolving;

    /// <summary>
    /// "40 achievements" when Steam has the schema cached, otherwise a hint that we don't know yet.
    /// A count of 0 is genuinely unknown here, not "no achievements": it just means Steam has never
    /// fetched this game's stats on this machine.
    /// </summary>
    public string CountLabel => AchievementCount > 0
        ? string.Format(Resources.Strings.Ach_Card_Count, AchievementCount)
        : Resources.Strings.Ach_Card_CountUnknown;

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        AppIdText.Contains(query, StringComparison.Ordinal);

    /// <summary>Resolve this game's cover once (disk → CDN → appdetails), reusing the Manage page's logic.</summary>
    public async Task EnsureCoverAsync(SteamAppInfoCache appInfo, CoverCache covers)
    {
        if (Cover is not null) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = await LuaTileViewModel.ResolveCoverFileAsync(AppId, appInfo, covers);
            if (local is not null) Cover = local;
        }
        catch { /* no cover is a cosmetic problem only */ }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }
}

/// <summary>One achievement row in the per-game flyout.</summary>
public partial class AchievementItemVm : ObservableObject
{
    private readonly long _appId;
    private readonly AchievementIconCache _icons;
    private readonly SteamAchievement _source;

    public AchievementItemVm(SteamAchievement source, long appId, AchievementIconCache icons)
    {
        _source = source;
        _appId = appId;
        _icons = icons;
        _isAchieved = source.IsAchieved;
    }

    public string Id => _source.Id;
    public string Name => _source.Name;
    public string Description => _source.Description;
    public bool IsHidden => _source.IsHidden;

    /// <summary>Server-awarded: Steam ignores any client attempt to change it, so the row is read-only.</summary>
    public bool IsProtected => _source.IsProtected;
    public bool CanToggle => !IsProtected;

    /// <summary>The state Steam reported at load time. Anything else is an unsaved change.</summary>
    public bool OriginalAchieved => _source.IsAchieved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private bool _isAchieved;

    [ObservableProperty] private string? _icon;

    public bool IsChanged => IsAchieved != OriginalAchieved;

    /// <summary>Unlock date, or empty while locked. Hidden achievements keep their description hidden
    /// until unlocked, exactly as Steam shows them.</summary>
    public string UnlockLabel => _source.UnlockedAt is { } when
        ? string.Format(Resources.Strings.Ach_UnlockedOn, when.ToString("d MMM yyyy HH:mm"))
        : "";

    public string DisplayDescription => IsHidden && !OriginalAchieved && Description.Length == 0
        ? Resources.Strings.Ach_HiddenDescription
        : Description;

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Id.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cache and show the icon for the current state: Steam ships a colour icon and a grey one, and
    /// swapping between them is what makes a toggled row read as unlocked at a glance.
    /// </summary>
    public async Task EnsureIconAsync()
    {
        string wanted = IsAchieved ? _source.Icon : _source.IconLocked;
        if (string.IsNullOrEmpty(wanted)) wanted = _source.Icon;

        string? path = await _icons.EnsureAsync(_appId, wanted);
        if (path is not null) Icon = path;
    }

    partial void OnIsAchievedChanged(bool value) => _ = EnsureIconAsync();
}

/// <summary>
/// "Achievements" page: pick a game from the Steam library, then unlock/lock its achievements and save
/// the change back to Steam.
///
/// <para>
/// None of the Steam work happens in this process. Every game gets a <see cref="AchievementSession"/>,
/// i.e. a short-lived x86 helper (<c>LuaTools.SamHost.exe</c>, built on gibbed's Steam Achievement
/// Manager) that owns the Steam connection; this view model only drives it. Steam must be running and
/// signed in for any of it to work.
/// </para>
///
/// <para>
/// Toggling a row changes nothing on Steam's side: edits are staged here and only leave the machine on
/// Save, so the user can flip a dozen rows and still back out.
/// </para>
/// </summary>
public partial class AchievementsViewModel : PagedListViewModel<AchievementGameCardVm>
{
    private readonly AchievementHostService hosts;
    private readonly SteamLibraryService library;
    private readonly SteamAppListCache appList;
    private readonly SteamAppInfoCache appInfo;
    private readonly CoverCache covers;
    private readonly AchievementIconCache icons;
    private readonly ToastService toast;
    private readonly SettingsService settings;

    public AchievementsViewModel(
        AchievementHostService hosts, SteamLibraryService library, SteamAppListCache appList,
        SteamAppInfoCache appInfo, CoverCache covers, AchievementIconCache icons, ToastService toast,
        SettingsService settings)
    {
        this.hosts = hosts;
        this.library = library;
        this.appList = appList;
        this.appInfo = appInfo;
        this.covers = covers;
        this.icons = icons;
        this.toast = toast;
        this.settings = settings;
        InitPageSize(settings.AchievementsPageSize);
    }

    private List<AchievementGameCardVm> _allGames = [];
    private AchievementSession? _session;
    private CancellationTokenSource? _detailCts;

    protected override void SavePageSizeSetting(int size) => settings.AchievementsPageSize = size;

    /// <summary>Warm the covers of the freshly-shown page only (idempotent, off the UI thread).</summary>
    protected override void OnPageSliced(IReadOnlyList<AchievementGameCardVm> slice)
    {
        foreach (var game in slice) _ = game.EnsureCoverAsync(appInfo, covers);
    }

    /// <summary>False when LuaTools.SamHost.exe is missing from the install: the page can't do anything.</summary>
    public bool IsHostAvailable => AchievementHostService.IsAvailable;

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // ── Game list ────────────────────────────────────────────────────

    /// <param name="force">True for the Refresh button; otherwise the list is built once per session.</param>
    public async Task LoadAsync(bool force = false)
    {
        if (!force && _allGames.Count > 0) return;
        if (!IsHostAvailable)
        {
            EmptyMessage = Resources.Strings.Ach_Err_HostMissing;
            return;
        }

        IsLoading = true;
        try
        {
            // Two sources, both local: what's installed right now, and every game Steam has ever cached
            // an achievement schema for (i.e. played on this machine, installed or not).
            var installed = await Task.Run(() => library.EnumerateInstalled().ToList());
            var schemas = await hosts.ScanSchemasAsync();
            await appList.EnsureLoadedAsync();

            var names = installed.ToDictionary(g => g.AppId, g => g.Name);
            var appIds = new HashSet<long>(names.Keys);
            appIds.UnionWith(schemas.Keys);

            _allGames = appIds
                .Select(id => new AchievementGameCardVm(
                    id,
                    names.TryGetValue(id, out var name) ? name : appList.GetName(id) ?? id.ToString(),
                    schemas.TryGetValue(id, out int count) ? count : 0,
                    names.ContainsKey(id)))
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            EmptyMessage = Resources.Strings.Ach_Empty_None;
            ApplyFilter();
        }
        catch
        {
            EmptyMessage = Resources.Strings.Ach_Err_Load;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (SearchText.Length > 0) SearchText = "";
        await LoadAsync(force: true);
    });

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        IEnumerable<AchievementGameCardVm> shown = _allGames;
        if (query.Length > 0) shown = shown.Where(g => g.Matches(query));
        SetFiltered(shown);
    }

    // ── Detail flyout ────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private AchievementGameCardVm? _selectedGame;

    public bool IsDetailOpen => SelectedGame is not null;

    /// <summary>The filtered rows shown in the flyout.</summary>
    public ObservableCollection<AchievementItemVm> Achievements { get; } = [];

    private List<AchievementItemVm> _allAchievements = [];

    [ObservableProperty] private bool _isLoadingAchievements;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;

    public bool NotBusy => !IsBusy;

    /// <summary>Localized reason the flyout has nothing to show (Steam closed, no schema, …).</summary>
    [ObservableProperty] private string? _detailError;

    [ObservableProperty] private string _achievementSearch = "";
    partial void OnAchievementSearchChanged(string value) => ApplyAchievementFilter();

    /// <summary>Row filter: null = all, true = unlocked only, false = locked only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterUnlocked))]
    [NotifyPropertyChangedFor(nameof(IsFilterLocked))]
    private bool? _stateFilter;

    public bool IsFilterAll => StateFilter is null;
    public bool IsFilterUnlocked => StateFilter == true;
    public bool IsFilterLocked => StateFilter == false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(ChangeLabel))]
    private int _unlockedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(ChangeLabel))]
    private int _changedCount;

    public string ProgressLabel => string.Format(Resources.Strings.Ach_Progress, UnlockedCount, TotalCount);
    public bool HasChanges => ChangedCount > 0;
    public string ChangeLabel => string.Format(Resources.Strings.Ach_Unsaved, ChangedCount);

    [RelayCommand]
    private async Task OpenGame(AchievementGameCardVm game)
    {
        CloseSession();
        SelectedGame = game;
        DetailError = null;
        Achievements.Clear();
        _allAchievements = [];
        AchievementSearch = "";
        StateFilter = null;
        UnlockedCount = TotalCount = ChangedCount = 0;
        _ = game.EnsureCoverAsync(appInfo, covers);

        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;

        IsLoadingAchievements = true;
        try
        {
            _session = await hosts.OpenAsync(game.AppId, ct);
            var achievements = await _session.ListAsync(ct);

            _allAchievements = achievements
                .Select(a => new AchievementItemVm(a, game.AppId, icons))
                .ToList();

            foreach (var item in _allAchievements) item.PropertyChanged += OnAchievementChanged;

            TotalCount = _allAchievements.Count;
            RecountState();
            ApplyAchievementFilter();

            if (TotalCount == 0) DetailError = Resources.Strings.Ach_Detail_None;
        }
        catch (OperationCanceledException)
        {
            // Flyout closed while loading: nothing to report.
        }
        catch (AchievementHostException ex)
        {
            DetailError = Describe(ex);
            CloseSession();
        }
        catch (Exception ex)
        {
            DetailError = ex.Message;
            CloseSession();
        }
        finally
        {
            IsLoadingAchievements = false;
        }
    }

    private void OnAchievementChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AchievementItemVm.IsAchieved)) return;
        RecountState();

        // A row can fall out of the current filter the moment it's toggled ("locked only" + unlock it).
        // Re-filtering there would make rows vanish under the cursor, so the list is left alone until
        // the user changes the filter themselves.
    }

    private void RecountState()
    {
        UnlockedCount = _allAchievements.Count(a => a.IsAchieved);
        ChangedCount = _allAchievements.Count(a => a.IsChanged);
    }

    [RelayCommand]
    private void SelectStateFilter(string? which)
    {
        StateFilter = which switch
        {
            "unlocked" => true,
            "locked" => false,
            _ => null,
        };
        ApplyAchievementFilter();
    }

    private void ApplyAchievementFilter()
    {
        string query = AchievementSearch.Trim();
        IEnumerable<AchievementItemVm> shown = _allAchievements;
        if (StateFilter is { } wantAchieved) shown = shown.Where(a => a.IsAchieved == wantAchieved);
        if (query.Length > 0) shown = shown.Where(a => a.Matches(query));

        Achievements.Clear();
        foreach (var item in shown)
        {
            Achievements.Add(item);
            _ = item.EnsureIconAsync();
        }
    }

    /// <summary>Stage every togglable row at once. Still just a staged change: Save is what commits it.</summary>
    [RelayCommand]
    private void UnlockAll() => SetAllLocal(true);

    [RelayCommand]
    private void LockAll() => SetAllLocal(false);

    private void SetAllLocal(bool achieved)
    {
        foreach (var item in _allAchievements.Where(a => a.CanToggle)) item.IsAchieved = achieved;
    }

    /// <summary>Drop every staged change and go back to what Steam reported.</summary>
    [RelayCommand]
    private void Revert()
    {
        foreach (var item in _allAchievements) item.IsAchieved = item.OriginalAchieved;
    }

    /// <summary>
    /// Push the staged changes to Steam, then reload so the list shows Steam's own view (real unlock
    /// timestamps included) rather than what we hoped we set.
    /// </summary>
    [RelayCommand]
    private async Task Save()
    {
        if (_session is null || IsBusy || !HasChanges || SelectedGame is not { } game) return;

        var changed = _allAchievements.Where(a => a.IsChanged).ToList();
        var settable = _allAchievements.Where(a => a.CanToggle).ToList();

        IsBusy = true;
        try
        {
            var ct = _detailCts?.Token ?? CancellationToken.None;

            // Unlock-all / lock-all is one command instead of hundreds of round trips.
            bool allOneWay = changed.Count == settable.Count && settable.Count > 0 &&
                             settable.All(a => a.IsAchieved == settable[0].IsAchieved);
            if (allOneWay)
            {
                await _session.SetAllAsync(settable[0].IsAchieved, ct);
            }
            else
            {
                foreach (var item in changed) await _session.SetAsync(item.Id, item.IsAchieved, ct);
            }

            await _session.StoreAsync(ct);

            int applied = changed.Count;
            await ReloadAchievementsAsync(ct);
            toast.Show(
                Resources.Strings.Ach_Toast_Saved,
                string.Format(Resources.Strings.Ach_Toast_Saved_Body, applied, game.Name));
        }
        catch (OperationCanceledException) { /* flyout closed mid-save */ }
        catch (AchievementHostException ex)
        {
            toast.Show(Resources.Strings.Ach_Toast_SaveFailed, Describe(ex), error: true);
        }
        catch (Exception ex)
        {
            toast.Show(Resources.Strings.Ach_Toast_SaveFailed, ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Wipe this game's progress. Steam's reset always clears the numeric stats too, so the confirmation
    /// says so outright; there is no achievements-only reset in the API.
    /// </summary>
    [RelayCommand]
    private async Task ResetProgress()
    {
        if (_session is null || IsBusy || SelectedGame is not { } game) return;

        var confirm = MessageBox.Show(
            string.Format(Resources.Strings.Ach_Reset_Ask, game.Name),
            Resources.Strings.Ach_Reset_Title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var ct = _detailCts?.Token ?? CancellationToken.None;
            await _session.ResetAsync(achievementsToo: true, ct);
            await ReloadAchievementsAsync(ct);
            toast.Show(Resources.Strings.Ach_Reset_Title, string.Format(Resources.Strings.Ach_Reset_Done, game.Name));
        }
        catch (OperationCanceledException) { /* flyout closed mid-reset */ }
        catch (AchievementHostException ex)
        {
            toast.Show(Resources.Strings.Ach_Toast_SaveFailed, Describe(ex), error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-read the list from Steam through the existing session (no respawn).</summary>
    private async Task ReloadAchievementsAsync(CancellationToken ct)
    {
        if (_session is null || SelectedGame is not { } game) return;

        var achievements = await _session.ListAsync(ct);

        foreach (var item in _allAchievements) item.PropertyChanged -= OnAchievementChanged;
        _allAchievements = achievements.Select(a => new AchievementItemVm(a, game.AppId, icons)).ToList();
        foreach (var item in _allAchievements) item.PropertyChanged += OnAchievementChanged;

        TotalCount = _allAchievements.Count;
        RecountState();
        ApplyAchievementFilter();
    }

    [RelayCommand]
    private void CloseDetail()
    {
        CloseSession();
        SelectedGame = null;
        Achievements.Clear();
        _allAchievements = [];
        DetailError = null;
    }

    /// <summary>Cancel anything in flight and stop the helper process. Safe to call repeatedly.</summary>
    private void CloseSession()
    {
        foreach (var item in _allAchievements) item.PropertyChanged -= OnAchievementChanged;

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// Turn a host failure code into something a user can act on. The codes are stable; the exception's
    /// own English message is only a fallback for the ones we don't have a phrasing for.
    /// </summary>
    private static string Describe(AchievementHostException ex) => ex.Code switch
    {
        "steam_not_running" or "host_gone" => Resources.Strings.Ach_Err_SteamNotRunning,
        "steam_not_found" => Resources.Strings.Ach_Err_SteamNotFound,
        "not_logged_in" => Resources.Strings.Ach_Err_NotLoggedIn,
        "appid_mismatch" => Resources.Strings.Ach_Err_AppIdMismatch,
        "no_schema" => Resources.Strings.Ach_Err_NoSchema,
        "stats_timeout" or "host_timeout" => Resources.Strings.Ach_Err_Timeout,
        "stats_error" or "stats_request_failed" => Resources.Strings.Ach_Err_Stats,
        "host_missing" or "host_start_failed" => Resources.Strings.Ach_Err_HostMissing,
        "protected_achievement" => Resources.Strings.Ach_Err_Protected,
        _ => string.IsNullOrWhiteSpace(ex.Message) ? Resources.Strings.Ach_Err_Unknown : ex.Message,
    };
}
