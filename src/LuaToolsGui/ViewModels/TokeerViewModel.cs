using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The Tokeer page: turn a shared activation code into a game you can launch.
/// </summary>
/// <remarks>
/// <para>Redeeming is done here, in the app: the code store is asked for the tickets the code stands
/// for, and they are written into Steam's credential store. Generating a code is not, and cannot
/// honestly be faked - it needs an ownership ticket taken from the live Steam session, which their app
/// gets from a helper published without source. The page says so and offers their app for that half
/// rather than pretending the button is missing.</para>
/// </remarks>
public partial class TokeerViewModel(TokeerService tokeer, ToastService toast) : ObservableObject
{
    /// <summary>Set by App: hand the Generate half over to the Downloads page, which owns their tool.</summary>
    public Action? OpenTokeerApp { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedeemCommand))]
    private string _code = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedeemCommand))]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;

    public bool NotBusy => !IsBusy;

    /// <summary>The last outcome, in the page rather than in a toast that scrolls away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    [ObservableProperty] private bool _statusIsError;

    public bool HasStatus => Status.Length > 0;

    private bool CanRedeem() => !IsBusy && Code.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRedeem))]
    private async Task Redeem()
    {
        IsBusy = true;
        Status = "";

        try
        {
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
                Status = result.Error ?? Resources.Strings.Tokeer_Err_Refused;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Open their app, which is the only thing that can generate a code.</summary>
    [RelayCommand]
    private void OpenGenerator() => OpenTokeerApp?.Invoke();
}
