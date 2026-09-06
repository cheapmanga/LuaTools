using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class AddonsView : UserControl
{
    public AddonsView(AddonsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Re-read the folder every time the page is shown: dropping a data addon in and coming back
        // here is the natural gesture, and it should be enough. Cheap - a handful of small json files.
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }
}
