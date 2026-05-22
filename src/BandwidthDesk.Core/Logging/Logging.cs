using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace BandwidthDesk.Core.Logging;

public static class Logging
{
    private static bool _initialized;
    private static readonly object _gate = new();

    public static ILogger Configure(string? logDirectory = null)
    {
        lock (_gate)
        {
            if (_initialized)
                return Log.Logger;

            logDirectory ??= AppPaths.LogDirectory;
            var logFile = Path.Combine(logDirectory, "bandwidthdesk-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.WithProperty("App", "BandwidthDesk")
                .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(
                    path: logFile,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
                .CreateLogger();

            _initialized = true;
            Log.Information("Logging initialized; file={LogFile}", logFile);
            return Log.Logger;
        }
    }

    public static void Shutdown()
    {
        Log.CloseAndFlush();
    }
}
