using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class ValidatorView : UserControl
{
    private readonly ValidatorViewModel _viewModel;

    /// <summary>Set while a scroll is already queued, so a burst of lines schedules one, not hundreds.</summary>
    private bool _scrollQueued;

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
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollQueued) return;

        // Coalesced and at Background priority: a chatty script adds lines far faster than the eye
        // reads them, and one scroll per line would spend the UI thread on nothing else.
        _scrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollQueued = false;
            if (_viewModel.Log.Count > 0) LogList.ScrollIntoView(_viewModel.Log[^1]);
        }, DispatcherPriority.Background);
    }
}
