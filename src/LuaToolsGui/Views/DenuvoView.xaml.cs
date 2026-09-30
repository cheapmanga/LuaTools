using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace LuaToolsGui.Views;

/// <summary>
/// One "Denuvo" page fronting the activation flow — the Validator (repair activation, shown by
/// default), Tokeer (share/redeem codes) and the EA token tab (write a token into an EA game's
/// emulator config). Once mounted, the child views stay put and are switched by visibility, so
/// flipping between them never re-runs their load or discards in-progress input. Tokeer is mounted
/// only on its first reveal, so opening the page doesn't run Tokeer's load (which fetches the
/// installed-games list) while it is still hidden.
/// </summary>
public partial class DenuvoView : UserControl
{
    private enum Tab { Validator, Tokeer, EaToken }

    private readonly TokeerView _tokeer;
    private bool _tokeerMounted;

    public DenuvoView(TokeerView tokeer, ValidatorView validator, EaTokenView eaToken)
    {
        InitializeComponent();
        _tokeer = tokeer;
        ValidatorHost.Content = validator; // the default tab: mounted (and loaded) right away
        EaTokenHost.Content = eaToken;     // no load of its own, so nothing to defer
    }

    private void TabTokeer_Click(object sender, RoutedEventArgs e) => Show(Tab.Tokeer);

    private void TabValidator_Click(object sender, RoutedEventArgs e) => Show(Tab.Validator);

    private void TabEaToken_Click(object sender, RoutedEventArgs e) => Show(Tab.EaToken);

    private void Show(Tab tab)
    {
        if (tab == Tab.Tokeer && !_tokeerMounted)
        {
            TokeerHost.Content = _tokeer; // first reveal: mount now, so its load runs here, not at page open
            _tokeerMounted = true;
        }

        Select(tab == Tab.Validator, ValidatorHost, TabValidator);
        Select(tab == Tab.Tokeer, TokeerHost, TabTokeer);
        Select(tab == Tab.EaToken, EaTokenHost, TabEaToken);
    }

    private static void Select(bool selected, ContentControl host, Wpf.Ui.Controls.Button tab)
    {
        host.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        tab.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }
}
