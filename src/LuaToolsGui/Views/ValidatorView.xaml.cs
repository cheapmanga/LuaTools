using System.Collections.Specialized;
using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class ValidatorView : UserControl
{
    private readonly ValidatorViewModel _viewModel;

    public ValidatorView(ValidatorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        // Reading a log that stops following the output defeats the point of showing it live.
        _viewModel.Log.CollectionChanged += ScrollToEnd;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(LogScroller.ScrollToEnd);
    }
}
