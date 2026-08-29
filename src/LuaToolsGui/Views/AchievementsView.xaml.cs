using System.Windows.Controls;
using System.Windows.Input;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class AchievementsView : UserControl
{
    private readonly AchievementsViewModel _viewModel;

    public AchievementsView(AchievementsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // On a page change, jump the grid back to the top (the new page starts fresh).
        _viewModel.ScrollToTop = ScrollGridToTop;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    /// <summary>Scroll the game grid back to offset 0, once the new page's items are laid out.</summary>
    private void ScrollGridToTop() => Dispatcher.BeginInvoke(() =>
    {
        if (FindDescendant<ScrollViewer>(GameScroller) is { } scroller) scroller.ScrollToTop();
    }, System.Windows.Threading.DispatcherPriority.Background);

    private static T? FindDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
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

    private void Scrim_Click(object sender, MouseButtonEventArgs e) =>
        _viewModel.CloseDetailCommand.Execute(null);

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) =>
        _viewModel.CloseDetailCommand.Execute(null);
}
