using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // Nothing may escape an async handler: with no global exception handler, a throw here would
        // close the app rather than leave an empty picker.
        Loaded += async (_, _) =>
        {
            try { await _viewModel.LoadAsync(); }
            catch { /* the picker stays empty; the page is still usable */ }
        };
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

    /// <summary>
    /// Hand the wheel back to the page once the log has nothing left to scroll.
    /// </summary>
    /// <remarks>
    /// A ScrollViewer marks every wheel event handled, even at its extremes, so with the pointer over
    /// the log the page underneath would not move - and the report code below it could not be reached
    /// without first moving the pointer off the log.
    /// </remarks>
    private void Log_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject el) return;

        var scroller = FindDescendant<ScrollViewer>(el);
        if (scroller is null) return;

        bool atTop = scroller.VerticalOffset <= 0;
        bool atBottom = scroller.VerticalOffset >= scroller.ScrollableHeight;
        if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom)) return; // the log still has room

        e.Handled = true;
        ((UIElement)sender).RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }

        return null;
    }
}
