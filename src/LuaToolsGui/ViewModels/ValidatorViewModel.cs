using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>One installed game, for a page's game picker.</summary>
public record InstalledGame(long AppId, string Name)
{
    public string Display => $"{Name} ({AppId})";
}

/// <summary>
/// The Validator page: runs the Denuvo activation check and repair on one installed game, and shows
/// what it is doing while it does it.
/// </summary>
/// <remarks>
/// <para>The work itself belongs to a script this app does not own and does not pin
/// (<see cref="DevuvoService"/>). What is added here is the part their own front end cannot have: the
/// game is picked from the user's actual Steam library rather than typed as a number, and the run is
/// gated behind a consent step that says what the report contains.</para>
///
/// <para>That gate is not decoration. The run does two things the user cannot take back: it uploads a
/// machine report - MachineGuid, disk serial, MAC addresses, public IP, the game's folder contents -
/// to a paste service whose public address IS the D-Report code, and it turns Smart App Control off,
/// which the script needs and which Windows will not turn back on without a clean reinstall. The
/// checkbox names both; consent is what carries the second into <see cref="DevuvoService"/>.</para>
/// </remarks>
public partial class ValidatorViewModel(
    DevuvoService devuvo, SteamLibraryService library, TokeerService tokeer, ToastService toast)
    : ObservableObject
{
    // What the script prints for a UI to read. All of them are optional: a script version that stops
    // printing one simply leaves that part of the page quiet, which is why none of them gate the run.
    private static readonly Regex ProgressRegex = new(@"LUATOOLS_PROGRESS:(\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex ReportCodeRegex =
        new(@"D-Report\s+Code:\s*([A-Za-z0-9_-]{5,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PasteUrlRegex =
        new(@"paste\.rtech\.support/([A-Za-z0-9_-]{5,})(?:\.txt)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Keeps a long run from growing the log without bound; the tail is what matters.</summary>
    private const int MaxLogLines = 3000;

    public ObservableCollection<InstalledGame> Games { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private InstalledGame? _selectedGame;

    /// <summary>Pins the game to its installed build, so a Steam update can't wipe the activation.</summary>
    [ObservableProperty] private bool _lockVersion;

    /// <summary>Ticked once the user has read what the run uploads and that it turns Smart App Control off.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _consentGiven;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    private bool _isRunning;

    public bool NotRunning => !IsRunning;

    [ObservableProperty] private int _progress;

    /// <summary>False until the script prints its first progress marker, so the bar doesn't sit at 0.</summary>
    [ObservableProperty] private bool _hasProgress;

    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReportCode))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCodeCommand))]
    private string _reportCode = "";

    public bool HasReportCode => ReportCode.Length > 0;

    /// <summary>
    /// Fill the picker from the games Steam actually has installed.
    /// </summary>
    /// <remarks>
    /// Off the UI thread: this walks every library folder's appmanifests, which is quick on a handful
    /// of games and visibly not on a few hundred.
    /// </remarks>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    /// <summary>Cached so a second navigation joins the first enumeration instead of repeating it.</summary>
    private Task? _loading;

    private async Task LoadCoreAsync()
    {
        var installed = await Task.Run(() => library.EnumerateInstalled()
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

        foreach (var (appId, name) in installed)
            Games.Add(new InstalledGame(appId, name));
    }

    private bool CanRun() => !IsRunning && ConsentGiven && SelectedGame is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (SelectedGame is not { } game) return;

        IsRunning = true;
        HasProgress = false;
        Progress = 0;
        ReportCode = "";
        Log.Clear();
        Status = Resources.Strings.Val_Status_Running;

        try
        {
            // Consent covers turning Smart App Control off as well as the report upload - the checkbox
            // says so - and consent is required to reach here, so it is always the gate that is passed.
            var result = await devuvo.RunAsync(game.AppId, LockVersion, ConsentGiven, OnLine);
            Status = result switch
            {
                DevuvoRunResult.Finished => HasReportCode
                    ? Resources.Strings.Val_Status_Done
                    : Resources.Strings.Val_Status_DoneNoCode,
                DevuvoRunResult.ElevationDeclined => Resources.Strings.Val_Status_NoElevation,
                DevuvoRunResult.ScriptUnavailable => Resources.Strings.Val_Status_NoScript,
                _ => Resources.Strings.Val_Status_Failed,
            };
        }
        catch (Exception)
        {
            // Nothing may escape an async command: there is no handler above it, so an exception here
            // would close the app instead of failing the run.
            Status = Resources.Strings.Val_Status_Failed;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// One transcript line, from the thread following the file. Everything here has to hop to the UI.
    /// </summary>
    /// <remarks>
    /// Posted rather than sent: a blocking Invoke per line makes the reader thread wait on a busy UI
    /// thread for every line of a chatty run. Posts keep their order, so the log still reads in
    /// sequence.
    /// </remarks>
    private void OnLine(string line)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log.Add(line);
            while (Log.Count > MaxLogLines) Log.RemoveAt(0);

            if (ProgressRegex.Match(line) is { Success: true } p
                && int.TryParse(p.Groups[1].Value, out int percent))
            {
                Progress = Math.Clamp(percent, 0, 100);
                HasProgress = true;
            }

            // The code and the paste address are the same identifier; whichever the script prints
            // first is the one the user needs.
            if (ReportCode.Length == 0)
            {
                var code = ReportCodeRegex.Match(line);
                if (!code.Success) code = PasteUrlRegex.Match(line);
                if (code.Success) ReportCode = code.Groups[1].Value;
            }
        });
    }

    private bool CanCopyReportCode() => HasReportCode;

    [RelayCommand(CanExecute = nameof(CanCopyReportCode))]
    private void CopyReportCode()
    {
        try
        {
            Clipboard.SetText(ReportCode);
            toast.Show(Resources.Strings.Val_Toast_Copied, ReportCode);
        }
        catch
        {
            // The clipboard can be held by another process. The code is on screen either way.
        }
    }

    // ── Apply the code the bot sends back ────────────────────────────
    // The Denuvo round-trip is: run above → send the D-Report code to the bot → the bot replies with a
    // code → apply it here. That last step is a Tokeer redeem (writes the tickets into Steam), brought
    // onto this page so the whole flow lives in one place instead of in a separate app.

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyBotCodeCommand))]
    [NotifyPropertyChangedFor(nameof(NotApplying))]
    private bool _isApplying;

    public bool NotApplying => !IsApplying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyBotCodeCommand))]
    private string _botCode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplyStatus))]
    private string _applyStatus = "";

    [ObservableProperty] private bool _applyIsError;

    public bool HasApplyStatus => ApplyStatus.Length > 0;

    private bool CanApplyBotCode() => !IsApplying && BotCode.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanApplyBotCode))]
    private async Task ApplyBotCode()
    {
        IsApplying = true;
        ApplyStatus = "";

        try
        {
            var result = await tokeer.RedeemAsync(BotCode);
            ApplyIsError = !result.Ok;
            if (result.Ok)
            {
                ApplyStatus = string.Format(Resources.Strings.Tokeer_Redeemed_Body, result.AppId);
                toast.Show(Resources.Strings.Tokeer_Redeemed_Title, ApplyStatus);
                BotCode = "";
            }
            else
            {
                // The store's own reason when it gave one (already used, wrong machine, expired…).
                ApplyStatus = string.IsNullOrWhiteSpace(result.Error)
                    ? Resources.Strings.Tokeer_Err_Refused
                    : result.Error!;
            }
        }
        catch (Exception)
        {
            // Nothing may escape an async command: there is no handler above it.
            ApplyIsError = true;
            ApplyStatus = Resources.Strings.Tokeer_Err_Unreachable;
        }
        finally
        {
            IsApplying = false;
        }
    }
}
