using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace LuaToolsGui.Views;

/// <summary>
/// One "Denuvo" page fronting the two halves of the activation flow — Tokeer (share/redeem codes) and
/// the Validator (repair activation). Both child views are hosted at once and switched by visibility, so
/// flipping between them never re-runs their load or discards in-progress input.
/// </summary>
public partial class DenuvoView : UserControl
{
    public DenuvoView(TokeerView tokeer, ValidatorView validator)
    {
        InitializeComponent();
        TokeerHost.Content = tokeer;
        ValidatorHost.Content = validator;
    }

    private void TabTokeer_Click(object sender, System.Windows.RoutedEventArgs e) => Show(tokeer: true);

    private void TabValidator_Click(object sender, System.Windows.RoutedEventArgs e) => Show(tokeer: false);

    private void Show(bool tokeer)
    {
        TokeerHost.Visibility = tokeer ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ValidatorHost.Visibility = tokeer ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        TabTokeer.Appearance = tokeer ? ControlAppearance.Primary : ControlAppearance.Secondary;
        TabValidator.Appearance = tokeer ? ControlAppearance.Secondary : ControlAppearance.Primary;
    }
}
