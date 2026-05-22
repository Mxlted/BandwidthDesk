using System;
using System.IO;

namespace BandwidthDesk.Core;

/// <summary>
/// Resolves the per-user data directory for BandwidthDesk under %LOCALAPPDATA%.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "BandwidthDesk";

    public static string DataDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, AppFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string RulesFilePath => Path.Combine(DataDirectory, "rules.json");
}
