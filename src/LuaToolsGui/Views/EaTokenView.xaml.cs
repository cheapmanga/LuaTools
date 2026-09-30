using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class EaTokenView : UserControl
{
    public EaTokenView(EaTokenViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
