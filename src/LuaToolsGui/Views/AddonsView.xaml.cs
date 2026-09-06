using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class AddonsView : UserControl
{
    public AddonsView(AddonsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
