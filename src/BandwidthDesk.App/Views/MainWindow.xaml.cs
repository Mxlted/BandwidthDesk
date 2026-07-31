using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BandwidthDesk.App.Services;
using BandwidthDesk.App.ViewModels;
using WpfListBoxItem = System.Windows.Controls.ListBoxItem;
using WpfTreeViewItem = System.Windows.Controls.TreeViewItem;

namespace BandwidthDesk.App.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(App.Services);
        DataContext = ViewModel;

        // Win11 title-bar follows the app palette via DWM. Required when elevated since the
        // "Administrator: " caption is the most visible chunk of system chrome.
        WindowChrome.ApplyTheme(this, ThemeManager.Current);
        ThemeManager.Changed += (_, t) => Dispatcher.Invoke(() => WindowChrome.ApplyTheme(this, t));
        App.Services.TrayIcon.RestoreRequested += TrayIcon_RestoreRequested;
        App.Services.TrayIcon.ExitRequested += TrayIcon_ExitRequested;
        App.Services.TrayIcon.ApplySettings(UserSettingsStore.Load());
        System.Windows.Application.Current.SessionEnding += MainWindow_SessionEnding;

        SourceInitialized += (_, _) => RestoreWindowPlacement();
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var settings = UserSettingsStore.Load();
        if (WindowState == WindowState.Minimized && settings.MinimizeToTray)
            HideToTray(settings);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            var settings = UserSettingsStore.Load();
            if (settings.CloseToTray)
            {
                e.Cancel = true;
                HideToTray(settings);
                return;
            }
        }

        SaveWindowPlacement();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ViewModel.Dispose();
        System.Windows.Application.Current.SessionEnding -= MainWindow_SessionEnding;
        App.Services.TrayIcon.RestoreRequested -= TrayIcon_RestoreRequested;
        App.Services.TrayIcon.ExitRequested -= TrayIcon_ExitRequested;
    }

    private void MainWindow_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _allowClose = true;
    }

    private void TrayIcon_RestoreRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RestoreFromTray);
    }

    private void TrayIcon_ExitRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _allowClose = true;
            Close();
        });
    }

    private void HideToTray(UserSettings settings)
    {
        SaveWindowPlacement();
        Hide();
        App.Services.TrayIcon.NotifyWindowHidden(settings);
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        App.Services.TrayIcon.NotifyWindowShown(UserSettingsStore.Load());
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
        var item = FindAncestor<WpfListBoxItem>(src);
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
        var item = FindAncestor<WpfTreeViewItem>(src);
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
        if (sender is WpfTreeViewItem tvi && !tvi.IsSelected)
        {
            tvi.Focus();
            tvi.IsSelected = true;
        }
    }

    private void RuleItem_OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfListBoxItem item && !item.IsSelected)
        {
            item.Focus();
            item.IsSelected = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
