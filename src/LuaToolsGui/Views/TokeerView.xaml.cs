using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class TokeerView : UserControl
{
    public TokeerView(TokeerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Nothing may escape an async handler: with no global exception handler, a throw here would
        // close the app rather than leave an empty picker.
        Loaded += async (_, _) =>
        {
            try { await viewModel.LoadAsync(); }
            catch { /* the picker stays empty; redeeming still works */ }
        };
    }
}
