using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class TokeerView : UserControl
{
    public TokeerView(TokeerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
