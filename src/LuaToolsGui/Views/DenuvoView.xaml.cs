using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace LuaToolsGui.Views;

/// <summary>
/// One "Denuvo" page fronting the two halves of the activation flow — the Validator (repair activation,
/// shown by default) and Tokeer (share/redeem codes). Once mounted, both child views stay put and are
/// switched by visibility, so flipping between them never re-runs their load or discards in-progress
/// input. Tokeer is mounted only on its first reveal, so opening the page doesn't run Tokeer's load
/// (which fetches the installed-games list) while it is still hidden.
/// </summary>
public partial class DenuvoView : UserControl
{
    private readonly TokeerView _tokeer;
    private bool _tokeerMounted;

    public DenuvoView(TokeerView tokeer, ValidatorView validator)
    {
        InitializeComponent();
        _tokeer = tokeer;
        ValidatorHost.Content = validator; // the default tab: mounted (and loaded) right away
    }

    private void TabTokeer_Click(object sender, System.Windows.RoutedEventArgs e) => Show(tokeer: true);

    private void TabValidator_Click(object sender, System.Windows.RoutedEventArgs e) => Show(tokeer: false);

    private void Show(bool tokeer)
    {
        if (tokeer && !_tokeerMounted)
        {
            TokeerHost.Content = _tokeer; // first reveal: mount now, so its load runs here, not at page open
            _tokeerMounted = true;
        }

        TokeerHost.Visibility = tokeer ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ValidatorHost.Visibility = tokeer ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        TabTokeer.Appearance = tokeer ? ControlAppearance.Primary : ControlAppearance.Secondary;
        TabValidator.Appearance = tokeer ? ControlAppearance.Secondary : ControlAppearance.Primary;
    }
}
