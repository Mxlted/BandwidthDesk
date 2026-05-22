using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using BandwidthDesk.App.Services;
using BandwidthDesk.App.Views;
using BandwidthDesk.Core.Logging;
using Serilog;

namespace BandwidthDesk.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    // Global\ prefix so the mutex is visible across sessions — relevant because the app runs
    // elevated and a second normal-token launch would otherwise get a different namespace.
    private const string SingleInstanceMutexName = @"Global\BandwidthDesk.SingleInstance.{8F2A6B1E-7C4D-4F9A-B3E1-2D5A9C8B7F03}";
    private const string SingleInstanceWindowTitle = "BandwidthDesk";
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            Shutdown();
            return;
        }

        Logging.Configure();
        Log.Information("BandwidthDesk starting; elevated={Elevated}", IsElevated());

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            ThemedDialog.Show(MainWindow, "BandwidthDesk error", args.Exception.Message,
                ThemedDialogKind.Error, ThemedDialogButtons.Ok);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled domain exception");
        };

        Services = new AppServices();
        Services.Initialize();

        // Apply persisted theme before any window opens.
        var settings = UserSettingsStore.Load();
        ThemeManager.Apply(settings.Theme);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services?.Dispose(); } catch { }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        Logging.Shutdown();
        base.OnExit(e);
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static void FocusExistingInstance()
    {
        // FindWindow by title — the main window keeps Title="BandwidthDesk" verbatim,
        // and there's only ever one because of the mutex above.
        var hwnd = FindWindow(null, SingleInstanceWindowTitle);
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
