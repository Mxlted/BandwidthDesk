# BandwidthDesk

A Windows app that caps how fast individual programs can upload and download. Think of it like a speed limit you can apply to just Chrome, or just a Steam download, without slowing anything else down.

> **Status:** v0.2. Works for IPv4 TCP/UDP traffic when the WinDivert driver files are present and the app is running as Administrator. See [Known limitations](#known-limitations).

---

## What it can do

* See every running program grouped by name, so all your Chrome windows show up as one row instead of fifteen.
* Watch live download and upload speeds for each program, updated every second.
* Add a rule for any program (by name, full path, or a specific running instance) with separate download and upload caps. Set a cap to 0 to leave that direction unlimited.
* Turn rules on and off with one click. Edit or delete them from a right-click menu or by double-clicking.
* Sort the process list by name, instance count, memory, or current traffic. Hide Microsoft system processes with one checkbox so the list isn't full of `svchost.exe`.
* Save the whole rule set as a named **profile**. Switch between profiles in a click (for example, a "Work" profile vs. a "Gaming" profile), or export them to a file and bring them to another PC.
* Dark, Light, and OLED themes. The Windows 11 title bar matches the theme.
* Adjustable refresh rate so the process list can poll faster on a fast PC or slower if you want to save CPU.

---

## What you need

* Windows 11 (Windows 10 22H2 probably works but hasn't been tested).
* The .NET 9 SDK if you want to build it yourself.
* The WinDivert driver files (see below). They aren't included.
* Administrator rights when you run the app, otherwise the limits can't be enforced.

### Getting WinDivert

BandwidthDesk uses [WinDivert](https://reqrypt.org/windivert.html), a small signed driver, to intercept and reinject network packets. It's the same library a lot of similar tools rely on, but it isn't redistributed here.

1. Grab the latest release from <https://reqrypt.org/windivert.html> or the [GitHub mirror](https://github.com/basil00/WinDivert/releases).
2. From the `x64/` folder of that release, copy these two files into `native/x64/` in this repo:
   * `WinDivert.dll`
   * `WinDivert64.sys`
3. When you build, they get copied next to the app automatically. The driver installs itself the first time the app starts capturing packets.

WinDivert is dual-licensed (LGPL or GPL). Check its license if you plan to redistribute it.

---

## Building

The easy way is `build.bat`:

```cmd
build.bat            :: Release build
build.bat debug      :: Debug build
build.bat run        :: Release build, then launch elevated
build.bat clean      :: clean every bin/ and obj/
```

The script will close any running copy of the app first so files aren't locked.

Without the script:

```powershell
dotnet restore
dotnet build -c Release
```

The exe lands at:

```
src\BandwidthDesk.App\bin\Release\net9.0-windows10.0.22000.0\BandwidthDesk.exe
```

If you dropped the WinDivert files into `native/x64/` before building, they'll be sitting right next to it.

---

## Running it

The app has to run as Administrator to capture packets. The bundled manifest asks for this automatically, so double-clicking it from Explorer gives you a UAC prompt and that's it.

From a terminal:

```powershell
Start-Process -Verb RunAs "src\BandwidthDesk.App\bin\Release\net9.0-windows10.0.22000.0\BandwidthDesk.exe"
```

Or just `build.bat run`.

If you start it without elevation, the UI still works for editing rules but a banner tells you the limits aren't actually being applied. Relaunch as admin when you're ready.

### Limiting a program

1. Find the program in the list on the left. Use the search box, or untick "Hide Microsoft" if you're looking for something system-level. Expand a group to pick a specific instance.
2. Click **Limit selected**, or right-click and choose **Add bandwidth limit**.
3. Type a download or upload cap. Leaving a value at 0 means "no limit" for that direction. The match defaults to the program's name, which means the rule will keep working when the program closes and opens again. Switch to PID if you only want to limit one specific run.
4. Save. The rule shows up on the right and starts applying right away.

You can also double-click a process to jump straight into the rule editor, or double-click an existing rule to edit it.

### Settings and profiles

The gear icon in the top right opens Settings, where you can:

* Switch theme.
* Change how often the process list refreshes.
* Pick the default unit (B/s, KB/s, MB/s) used when creating new rules.
* Save the current rules as a named profile, switch profiles, delete them, or export and import them as files.

Profiles live at `%LOCALAPPDATA%\BandwidthDesk\profiles\` and are plain JSON, so they're easy to back up or share.

---

## How it works (the short version)

The app sits between Windows and your network card, looking at every packet going in or out. For each packet it figures out which program owns it, checks if you have a rule for that program, and if so passes the packet through a "token bucket" that paces it to your chosen speed limit. If the bucket is empty the packet waits a few milliseconds. The remote side notices the slowdown and naturally throttles itself.

A separate background thread measures real throughput per program once a second, which is what feeds the live numbers in the UI even when no rule is matching.

### Why WinDivert?

There are a few ways to do per-program bandwidth limiting on Windows. WinDivert is the practical pick:

| Approach | Why not |
|---|---|
| Custom WFP kernel driver | Best precision, but you have to write, sign, and ship a kernel driver. Way too much overhead for a project this size. |
| Group Policy / QoS Policy | Built into Windows, but only handles outbound traffic and can't cap inbound at all. |
| **WinDivert** | Runs in user mode on top of a small signed driver. Handles inbound and outbound, TCP and UDP, and lets us match each packet to a program. |

The trade-off: WinDivert installs a kernel driver (requires admin), and adds a small amount of overhead per packet. Fine for normal use, not what you want if you're trying to saturate a 10 Gbps link.

---

## Where files go

| File | Purpose |
|---|---|
| `%LOCALAPPDATA%\BandwidthDesk\rules.json` | Your bandwidth rules |
| `%LOCALAPPDATA%\BandwidthDesk\settings.json` | Preferences (theme, sort order, refresh rate, etc.) |
| `%LOCALAPPDATA%\BandwidthDesk\profiles\<name>.json` | Saved profiles |
| `%LOCALAPPDATA%\BandwidthDesk\logs\bandwidthdesk-YYYY-MM-DD.log` | Daily rolling log files (kept for 7 days) |

If something's misbehaving, the log file is the first place to check. Every rule change and every error from the driver gets recorded there.

---

## Known limitations

* **IPv4 only for now.** IPv6 packets are seen but can't be tied back to a program yet, so they pass through unshaped.
* **No system / kernel traffic.** Anything owned by PID 0 or 4 (the kernel itself) can't be matched to a rule.
* **One rule per program wins.** The first matching rule is used. There's no chaining.
* **Best-effort PID matching.** The process-to-port lookup refreshes about every 750ms, so very short bursts may slip through before a rule catches them.
* **PID recycling.** Windows reuses process IDs. Match results are cached by PID, so a very long session may occasionally mis-attribute traffic until the rule set changes. Restart the app if that happens.
* **No installer.** Build and run for now. No MSIX or MSI yet.
* **Needs admin.** Without elevation the UI is read-only as far as the engine is concerned. Limits aren't enforced.

---

## License

[MIT](./LICENSE). WinDivert is licensed separately by its authors. See <https://reqrypt.org/windivert.html>.
