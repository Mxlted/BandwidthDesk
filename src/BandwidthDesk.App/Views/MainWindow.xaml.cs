using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BandwidthDesk.App.Services;
using BandwidthDesk.App.ViewModels;

namespace BandwidthDesk.App.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(App.Services);
        DataContext = ViewModel;

        // Win11 title-bar follows the app palette via DWM. Required when elevated since the
        // "Administrator: " caption is the most visible chunk of system chrome.
        WindowChrome.ApplyTheme(this, ThemeManager.Current);
        ThemeManager.Changed += (_, t) => Dispatcher.Invoke(() => WindowChrome.ApplyTheme(this, t));

        SourceInitialized += (_, _) => RestoreWindowPlacement();
        Closing += (_, _) => SaveWindowPlacement();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    private void RestoreWindowPlacement()
    {
        var s = UserSettingsStore.Load();

        if (s.WindowWidth is double sw && s.WindowHeight is double sh &&
            s.WindowLeft is double sl && s.WindowTop is double st)
        {
            var w = Math.Max(MinWidth, sw);
            var h = Math.Max(MinHeight, sh);

            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            // Keep at least a sliver of the window on-screen so it can be recovered if the
            // monitor layout changed since last run.
            const double margin = 40;
            if (sl + w < virtualLeft + margin || sl > virtualRight - margin ||
                st + h < virtualTop + margin || st > virtualBottom - margin)
            {
                SizeToWorkArea(0.75);
            }
            else
            {
                Width = w;
                Height = h;
                Left = sl;
                Top = st;
            }
        }
        else
        {
            SizeToWorkArea(0.75);
        }

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        // Re-load before mutating so we don't clobber settings the user toggled via the
        // Settings dialog (themes, sort prefs, etc.) since this window was opened.
        var s = UserSettingsStore.Load();
        s.WindowMaximized = WindowState == WindowState.Maximized;

        // RestoreBounds gives the non-maximized rectangle even when currently maximized.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (!bounds.IsEmpty)
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }

        UserSettingsStore.Save(s);
    }

    private void SizeToWorkArea(double fraction)
    {
        var work = SystemParameters.WorkArea; // primary monitor's usable area (excludes taskbar)
        var w = Math.Max(MinWidth, work.Width * fraction);
        var h = Math.Max(MinHeight, work.Height * fraction);
        Width = Math.Min(w, work.Width);
        Height = Math.Min(h, work.Height);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private void ProcessTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ViewModel.SelectedProcessNode = e.NewValue;
    }

    // Double-click on a rule row opens the editor. We only fire when an actual ListViewItem was hit
    // — clicks on the column header strip and empty space should be ignored.
    private void RulesList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var item = FindAncestor<ListViewItem>(src);
        if (item is null) return;
        if (ViewModel.EditRuleCommand.CanExecute(null))
            ViewModel.EditRuleCommand.Execute(null);
    }

    // Double-click on a process row opens the rule editor for that process (prefer an existing
    // rule, otherwise start a new one). Falls through to default expand/collapse behaviour for
    // group rows when the user clicked on the expand chevron area.
    private void ProcessTree_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var item = FindAncestor<TreeViewItem>(src);
        if (item is null) return;

        var node = item.DataContext;
        var existing = ViewModel.FindRuleForProcess(node);
        if (existing is not null)
        {
            ViewModel.SelectedRule = existing;
            if (ViewModel.EditRuleCommand.CanExecute(null))
                ViewModel.EditRuleCommand.Execute(null);
        }
        else
        {
            if (ViewModel.NewRuleFromProcessNodeCommand.CanExecute(node))
                ViewModel.NewRuleFromProcessNodeCommand.Execute(node);
        }
        e.Handled = true;
    }

    // Right-clicking a TreeViewItem should select it first; WPF doesn't do this by default,
    // which is why the selection looked like it was "disappearing" on right-click.
    private void ProcessItem_OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem tvi && !tvi.IsSelected)
        {
            tvi.Focus();
            tvi.IsSelected = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
