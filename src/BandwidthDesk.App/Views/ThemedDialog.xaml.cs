using System;
using System.Windows;
using BandwidthDesk.App.Services;

namespace BandwidthDesk.App.Views;

public enum ThemedDialogKind { Info, Question, Warning, Error }
public enum ThemedDialogButtons { Ok, OkCancel, YesNo }
public enum ThemedDialogResult { Ok, Yes, No, Cancel }

public partial class ThemedDialog : Window
{
    private ThemedDialogResult _result = ThemedDialogResult.Cancel;
    public ThemedDialogResult ResultKind => _result;

    public ThemedDialog()
    {
        InitializeComponent();
        WindowChrome.ApplyTheme(this, ThemeManager.Current);
        EventHandler<AppTheme> onThemeChanged = (_, t) =>
            Dispatcher.Invoke(() => WindowChrome.ApplyTheme(this, t));
        ThemeManager.Changed += onThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= onThemeChanged;
    }

    public static ThemedDialogResult Show(
        Window? owner, string header, string message,
        ThemedDialogKind kind = ThemedDialogKind.Info,
        ThemedDialogButtons buttons = ThemedDialogButtons.Ok)
    {
        var d = new ThemedDialog();
        if (owner is not null && !ReferenceEquals(owner, d)) d.Owner = owner;

        d.Title = "BandwidthDesk";
        d.HeaderText.Text = header;
        d.MessageText.Text = message;

        d.IconText.Text = kind switch
        {
            ThemedDialogKind.Question => "?",
            ThemedDialogKind.Warning => "!",
            ThemedDialogKind.Error => "×",
            _ => "i",
        };
        d.IconText.Foreground = kind switch
        {
            ThemedDialogKind.Warning => (System.Windows.Media.Brush)d.FindResource("Brush.Warning"),
            ThemedDialogKind.Error => (System.Windows.Media.Brush)d.FindResource("Brush.Danger"),
            _ => (System.Windows.Media.Brush)d.FindResource("Brush.Accent"),
        };

        switch (buttons)
        {
            case ThemedDialogButtons.Ok:
                d.PrimaryButton.Content = "OK";
                break;
            case ThemedDialogButtons.OkCancel:
                d.PrimaryButton.Content = "OK";
                d.CancelButton.Visibility = Visibility.Visible;
                break;
            case ThemedDialogButtons.YesNo:
                d.PrimaryButton.Content = "Yes";
                d.NoButton.Visibility = Visibility.Visible;
                break;
        }

        d.ShowDialog();
        return d._result;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _result = NoButton.Visibility == Visibility.Visible
            ? ThemedDialogResult.Yes
            : ThemedDialogResult.Ok;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        _result = ThemedDialogResult.No;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _result = ThemedDialogResult.Cancel;
        Close();
    }
}
