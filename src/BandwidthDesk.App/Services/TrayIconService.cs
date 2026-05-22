using System;
using System.Drawing;
using System.Windows;
using Serilog;
using Forms = System.Windows.Forms;

namespace BandwidthDesk.App.Services;

/// <summary>
/// Owns the Windows notification-area icon used for minimize/close-to-tray behavior.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly object _gate = new();
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private bool _windowHidden;
    private bool _trayHintShown;

    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_notifyIcon is not null)
                return;

            _icon = LoadIcon();

            var showItem = new Forms.ToolStripMenuItem("Show BandwidthDesk");
            showItem.Click += (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty);

            var exitItem = new Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

            _menu = new Forms.ContextMenuStrip();
            _menu.Items.Add(showItem);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _notifyIcon = new Forms.NotifyIcon
            {
                ContextMenuStrip = _menu,
                Icon = _icon,
                Text = "BandwidthDesk",
                Visible = false,
            };
            _notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplySettings(UserSettings settings)
    {
        EnsureCreated();
        SetVisible(settings.MinimizeToTray || settings.CloseToTray || _windowHidden);
    }

    public void NotifyWindowHidden(UserSettings settings)
    {
        EnsureCreated();
        _windowHidden = true;
        SetVisible(true);

        if (!settings.ShowTrayNotifications || _trayHintShown)
            return;

        _trayHintShown = true;
        try
        {
            _notifyIcon?.ShowBalloonTip(
                2500,
                "BandwidthDesk is still running",
                "Double-click the tray icon to restore it, or choose Exit from the tray menu.",
                Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to show tray notification");
        }
    }

    public void NotifyWindowShown(UserSettings settings)
    {
        _windowHidden = false;
        ApplySettings(settings);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _menu?.Dispose();
            _menu = null;

            _icon?.Dispose();
            _icon = null;
        }
    }

    private void EnsureCreated()
    {
        if (_notifyIcon is null)
            Initialize();
    }

    private void SetVisible(bool visible)
    {
        lock (_gate)
        {
            if (_notifyIcon is not null)
                _notifyIcon.Visible = visible;
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/icon.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
                return new Icon(resource.Stream);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to load tray icon resource");
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
