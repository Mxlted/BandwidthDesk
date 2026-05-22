using System;
using System.Windows;
using BandwidthDesk.App.Services;
using BandwidthDesk.App.ViewModels;

namespace BandwidthDesk.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppServices services)
    {
        InitializeComponent();
        // Settings VM owns its own UserSettings copy; we save through the static store so the
        // change is durable and instantly reflected by anything else reading from disk.
        DataContext = new SettingsViewModel(
            services,
            UserSettingsStore.Load,
            UserSettingsStore.Save);

        WindowChrome.ApplyTheme(this, ThemeManager.Current);
        EventHandler<AppTheme> onThemeChanged = (_, t) =>
            Dispatcher.Invoke(() => WindowChrome.ApplyTheme(this, t));
        ThemeManager.Changed += onThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= onThemeChanged;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
