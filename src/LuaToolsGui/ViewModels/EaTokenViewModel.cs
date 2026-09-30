using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The "EA token" tab of the Denuvo page: pick an EA game's folder, paste a Denuvo token, and write it
/// into the emulator's <c>anadius.cfg</c> / <c>token.ini</c> through <see cref="EaConfigService"/>.
/// </summary>
/// <remarks>
/// EA games are usually installed by the EA app, not Steam, so the game is a folder rather than a
/// pick from the Steam library. The config files found in it are listed as soon as the folder is set,
/// so a wrong folder shows up before anything is written.
/// </remarks>
public partial class EaTokenViewModel(EaConfigService config, ToastService toast) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private string _gameFolder = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private string _token = "";

    /// <summary>The config files under <see cref="GameFolder"/>, relative to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoundText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private IReadOnlyList<string> _foundFiles = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyPropertyChangedFor(nameof(NotApplying))]
    private bool _isApplying;

    public bool NotApplying => !IsApplying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    [ObservableProperty] private bool _statusIsError;

    public bool HasStatus => Status.Length > 0;

    /// <summary>What the folder holds, under the folder box. Empty until a folder is set.</summary>
    public string FoundText => GameFolder.Trim().Length == 0 ? ""
        : FoundFiles.Count == 0 ? Resources.Strings.Ea_NotFound
        : string.Format(Resources.Strings.Ea_Found, string.Join(", ", FoundFiles));

    /// <summary>Bumped on every folder change, so a slow scan of an old folder can't overwrite a newer one.</summary>
    private int _scanVersion;

    /// <summary>The last folder scan, awaitable (tests, and nothing else, wait on it).</summary>
    public Task Scan { get; private set; } = Task.CompletedTask;

    partial void OnGameFolderChanged(string value)
    {
        Status = "";
        Scan = ScanAsync(value.Trim(), ++_scanVersion);
    }

    private async Task ScanAsync(string folder, int version)
    {
        IReadOnlyList<string> found = [];
        if (folder.Length > 0)
        {
            // Off the UI thread: the search is recursive, and a game folder can hold tens of thousands of files.
            found = await Task.Run(() => EaConfigService.FindConfigFiles(folder)
                .Select(p => Path.GetRelativePath(folder, p))
                .ToList());
        }

        if (version == _scanVersion) FoundFiles = found;
        OnPropertyChanged(nameof(FoundText));
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Ea_ChooseFolder,
            InitialDirectory = Directory.Exists(GameFolder) ? GameFolder : "",
        };
        if (dialog.ShowDialog() == true) GameFolder = dialog.FolderName;
    }

    private bool CanApply() => !IsApplying && Token.Trim().Length > 0 && FoundFiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        string folder = GameFolder.Trim();
        string token = Token.Trim();

        IsApplying = true;
        Status = "";

        IReadOnlyList<EaConfigFileResult> results;
        try
        {
            results = await Task.Run(() => config.ApplyToken(folder, token));
        }
        catch (ArgumentException)
        {
            StatusIsError = true;
            Status = Resources.Strings.Ea_Status_Invalid;
            return;
        }
        catch (Exception ex)
        {
            // Nothing may escape an async command: there is no handler above it. Usually a file that
            // is read-only or locked by the running game.
            StatusIsError = true;
            Status = string.Format(Resources.Strings.Ea_Status_Failed, ex.Message);
            return;
        }
        finally
        {
            IsApplying = false;
        }

        // A file that already held this exact token counts as done: it's in the state asked for.
        int done = results.Count(r => r.Replacements > 0);
        StatusIsError = done == 0;
        if (results.Count == 0)
        {
            Status = Resources.Strings.Ea_NotFound;
        }
        else if (done == 0)
        {
            Status = string.Format(Resources.Strings.Ea_Status_NothingToReplace, results.Count);
        }
        else
        {
            Status = string.Format(Resources.Strings.Ea_Status_Done, done);
            // Outside the try on purpose: the files are written by now, so a toast that can't show must
            // not turn that into a "couldn't write" status. It still must not escape the command.
            try { toast.Show(Resources.Strings.Ea_Toast_Done, Status); } catch { /* the status line says it */ }
        }
    }
}
