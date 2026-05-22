using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using BandwidthDesk.Core.Models;
using Serilog;

namespace BandwidthDesk.Core.Processes;

[SupportedOSPlatform("windows")]
public sealed class ProcessService
{
    /// <summary>
    /// Enumerates running processes that look like user-facing apps or services we might want to limit.
    /// Skips System Idle Process (PID 0). Best-effort: some processes will refuse access to MainModule.
    /// </summary>
    public IReadOnlyList<ProcessInfo> GetProcesses(bool includeSystem = false)
    {
        var result = new List<ProcessInfo>(256);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0)
                    continue;

                string name;
                try { name = p.ProcessName; }
                catch { name = $"pid-{p.Id}"; }

                if (!includeSystem && IsSystemProcess(name))
                {
                    continue;
                }

                string? path = null;
                string? description = null;
                string? company = null;
                try
                {
                    var mod = p.MainModule;
                    path = mod?.FileName;
                    var fvi = mod?.FileVersionInfo;
                    description = fvi?.FileDescription;
                    company = fvi?.CompanyName;
                }
                catch (Exception)
                {
                    // Access denied is common for processes owned by other users / SYSTEM.
                }

                long ws = 0;
                try { ws = p.WorkingSet64; } catch { /* ignore */ }

                bool isMs = IsMicrosoftProcess(path, company);
                result.Add(new ProcessInfo(p.Id, name, path, description, company, ws, isMs));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Skipping process pid={Pid}", SafePid(p));
            }
            finally
            {
                p.Dispose();
            }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Returns the executable file name (without extension) for a given pid, or null if unavailable.
    /// </summary>
    public string? GetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    private static bool IsSystemProcess(string name)
    {
        // Very conservative blocklist; only the obvious ones the user can never limit usefully.
        return name.Equals("System", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Registry", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Secure System", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Heuristic: a process is "Microsoft / system" if its EXE lives under %SystemRoot%
    /// or its FileVersionInfo CompanyName starts with "Microsoft". This filters out the long
    /// tail of svchost / dllhost / system service processes a typical user never wants to cap.
    /// </summary>
    private static bool IsMicrosoftProcess(string? path, string? company)
    {
        if (!string.IsNullOrEmpty(company)
            && company.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(path))
        {
            var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(sysRoot)
                && path.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
