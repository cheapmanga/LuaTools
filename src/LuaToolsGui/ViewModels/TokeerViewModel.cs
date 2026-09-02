using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The Tokeer page: turn a shared activation code into a game you can launch, and mint one for a
/// game you own.
/// </summary>
/// <remarks>
/// Both halves work off the tickets Steam itself caches, so neither needs their app: redeeming writes
/// them, generating reads them back. Generating a game that has never been launched on this PC is the
/// one case with nothing to read, and it is reported as the precondition it is rather than as a
/// failure.
/// </remarks>
public partial class TokeerViewModel(
    TokeerService tokeer, UnlockerService unlocker, SteamLibraryService library, ToastService toast)
    : ObservableObject
{
    public ObservableCollection<InstalledGame> Games { get; } = [];

    // ── Redeem ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedeemCommand))]
    private string _code = "";

    // Both commands read IsBusy, so both have to be told when it moves. Without the second attribute
    // the Generate button stays live during a redeem - RelayCommand does not listen to
    // CommandManager.RequerySuggested - and the two runs would race to clear IsBusy.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedeemCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;

    public bool NotBusy => !IsBusy;

    /// <summary>The last outcome, in the page rather than in a toast that scrolls away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    [ObservableProperty] private bool _statusIsError;

    public bool HasStatus => Status.Length > 0;

    // ── Generate ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private InstalledGame? _selectedGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneratedCode))]
    [NotifyCanExecuteChangedFor(nameof(CopyGeneratedCodeCommand))]
    private string _generatedCode = "";

    public bool HasGeneratedCode => GeneratedCode.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenerateStatus))]
    private string _generateStatus = "";

    public bool HasGenerateStatus => GenerateStatus.Length > 0;

    /// <summary>Fill the picker from the games Steam has installed. Off the UI thread, like Validator.</summary>
    public async Task LoadAsync()
    {
        if (Games.Count > 0) return;

        var installed = await Task.Run(() => library.EnumerateInstalled()
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

        foreach (var (appId, name) in installed)
            Games.Add(new InstalledGame(appId, name));
    }

    private bool CanRedeem() => !IsBusy && Code.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRedeem))]
    private async Task Redeem()
    {
        IsBusy = true;
        Status = "";

        try
        {
            if (!await ConfirmWithoutModeAsync()) return;

            var result = await tokeer.RedeemAsync(Code);
            StatusIsError = !result.Ok;

            if (result.Ok)
            {
                Status = string.Format(Resources.Strings.Tokeer_Redeemed_Body, result.AppId);
                toast.Show(Resources.Strings.Tokeer_Redeemed_Title, Status);
                Code = "";
            }
            else
            {
                // The store's own wording when it gave one: it knows why far better than we do
                // (already redeemed, wrong machine, expired), and translating it away would lose that.
                Status = string.IsNullOrWhiteSpace(result.Error)
                    ? Resources.Strings.Tokeer_Err_Refused
                    : result.Error!;
            }
        }
        catch (Exception)
        {
            // Nothing may escape an async command: there is no handler above it, so an exception here
            // would close the app rather than fail the redeem.
            StatusIsError = true;
            Status = Resources.Strings.Tokeer_Err_Unreachable;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Ask before spending a code on a machine with no unlocker mode active. True = go ahead.
    /// </summary>
    /// <remarks>
    /// A code is single use and the store spends it the moment it answers. With no mode active the
    /// tickets are written to a Steam that will not use them, and the code is gone for nothing - the
    /// one failure a user cannot undo. Deliberately a question and not a block: if the detection is
    /// wrong, refusing to redeem would be worse than asking.
    /// </remarks>
    private async Task<bool> ConfirmWithoutModeAsync()
    {
        try
        {
            if (await unlocker.DetectActiveModeAsync() is not null) return true;
        }
        catch
        {
            return true; // couldn't tell. Not a reason to stand in the way
        }

        return MessageBox.Show(
            Resources.Strings.Tokeer_Warn_NoMode_Body,
            Resources.Strings.Tokeer_Warn_NoMode_Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool CanGenerate() => !IsBusy && SelectedGame is not null;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task Generate()
    {
        if (SelectedGame is not { } game) return;

        IsBusy = true;
        GenerateStatus = "";
        GeneratedCode = "";

        try
        {
            var result = await tokeer.GenerateAsync(game.AppId);
            if (result.Ok) GeneratedCode = result.Code;
            else GenerateStatus = string.IsNullOrWhiteSpace(result.Error)
                ? Resources.Strings.Tokeer_Err_NoCode
                : result.Error!;
        }
        catch (Exception)
        {
            GenerateStatus = Resources.Strings.Tokeer_Err_Unreachable;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCopyGeneratedCode() => HasGeneratedCode;

    [RelayCommand(CanExecute = nameof(CanCopyGeneratedCode))]
    private void CopyGeneratedCode()
    {
        try
        {
            Clipboard.SetText(GeneratedCode);
            toast.Show(Resources.Strings.Tokeer_Generated_Header, GeneratedCode);
        }
        catch
        {
            // The clipboard can be held by another process. The code is on screen either way.
        }
    }
}
