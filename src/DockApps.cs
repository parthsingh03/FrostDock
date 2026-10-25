using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace FrostDock;

/// <summary>One app shown in the dock.</summary>
internal sealed class DockApp
{
    public required string Name { get; init; }
    /// <summary>Full path to the exe, or a shell target like "ms-settings:" / "calc.exe".</summary>
    public required string LaunchTarget { get; init; }
    /// <summary>Process name (without .exe) used to detect running state, or null.</summary>
    public string? ProcessName { get; init; }
    /// <summary>Path used only to extract the icon, when LaunchTarget is a shell target.</summary>
    public string? IconPath { get; init; }
    /// <summary>Store/UWP AUMID (e.g. "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"), when known.</summary>
    public string? AumidHint { get; init; }
    public Bitmap? IconBitmap { get; set; }
    /// <summary>
    /// v1.2: synthetic dock entries (Launchpad, Trash) that run an action
    /// instead of launching an app. When set, LaunchOrFocus runs it.
    /// </summary>
    public Action? SpecialAction { get; init; }
    /// <summary>v1.2: true when IconBitmap is already finished tile artwork (Launchpad icon).</summary>
    public bool IconIsTile { get; init; }

    public Process? RunningProcess
    {
        get
        {
            if (ProcessName == null) return null;
            try
            {
                var procs = Process.GetProcessesByName(ProcessName);
                foreach (var p in procs)
                    if (p.MainWindowHandle != IntPtr.Zero) return p;
                return procs.Length > 0 ? procs[0] : null;
            }
            catch { return null; }
        }
    }

    public bool IsRunning => RunningProcess != null;

    public void LaunchOrFocus()
    {
        if (SpecialAction != null)
        {
            try { SpecialAction(); } catch { }
            return;
        }
        try
        {
            var running = RunningProcess;
            if (running != null && running.MainWindowHandle != IntPtr.Zero)
            {
                var h = running.MainWindowHandle;
                if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(h);
                return;
            }
        }
        catch { /* fall through to launch */ }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LaunchTarget,
                UseShellExecute = true
            });
        }
        catch { /* app missing - nothing sensible to do in v1 */ }
    }
}

internal static class DockAppDiscovery
{
    private static string? FirstExisting(params string?[] paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private static string? FromAppPaths(string exeName)
    {
        string[] roots = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName };
        foreach (var root in roots)
        {
            try
            {
                using var cu = Registry.CurrentUser.OpenSubKey(root);
                if (cu?.GetValue(null) is string v && File.Exists(v)) return v;
                using var lm = Registry.LocalMachine.OpenSubKey(root);
                if (lm?.GetValue(null) is string v2 && File.Exists(v2)) return v2;
            }
            catch { }
        }
        return null;
    }

    private static string PF(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), rel);
    private static string PF86(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), rel);
    private static string LocalApp(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel);
    private static string Roaming(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), rel);

    public static List<DockApp> DefaultApps()
    {
        var apps = new List<DockApp>();
        void Add(string name, string? exePath, string processName, string fallbackTarget)
        {
            apps.Add(new DockApp
            {
                Name = name,
                LaunchTarget = exePath ?? fallbackTarget,
                ProcessName = processName,
                IconPath = exePath
            });
        }

        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Add("Explorer",
            Path.Combine(win, "explorer.exe"), "explorer",
            Path.Combine(win, "explorer.exe"));

        var edge = FirstExisting(PF86(@"Microsoft\Edge\Application\msedge.exe"), PF(@"Microsoft\Edge\Application\msedge.exe"), FromAppPaths("msedge.exe"));
        var chrome = FirstExisting(PF(@"Google\Chrome\Application\chrome.exe"), PF86(@"Google\Chrome\Application\chrome.exe"), LocalApp(@"Google\Chrome\Application\chrome.exe"), FromAppPaths("chrome.exe"));
        if (chrome != null) Add("Chrome", chrome, "chrome", chrome);
        else if (edge != null) Add("Edge", edge, "msedge", edge);

        var code = FirstExisting(LocalApp(@"Programs\Microsoft VS Code\Code.exe"), PF(@"Microsoft VS Code\Code.exe"), FromAppPaths("Code.exe"));
        if (code != null) Add("VS Code", code, "Code", code);

        var spotify = FirstExisting(Roaming(@"Spotify\Spotify.exe"), LocalApp(@"Microsoft\WindowsApps\Spotify.exe"), FromAppPaths("Spotify.exe"));
        if (spotify != null) Add("Spotify", spotify, "Spotify", spotify);

        var terminal = FirstExisting(LocalApp(@"Microsoft\WindowsApps\wt.exe"));
        Add("Terminal", terminal, "WindowsTerminal", terminal ?? "wt.exe");

        Add("Notepad", Path.Combine(sys, "notepad.exe"), "notepad", "notepad.exe");

        apps.Add(new DockApp
        {
            Name = "Calculator",
            LaunchTarget = "calc.exe",
            ProcessName = "CalculatorApp",
            IconPath = Path.Combine(sys, "calc.exe"),
            // Well-known AUMID for the Store Calculator: its real icon lives here,
            // not in the calc.exe stub (which yields the old monochrome glyph).
            AumidHint = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
        });

        var settingsExe = Path.Combine(win, "ImmersiveControlPanel", "SystemSettings.exe");
        apps.Add(new DockApp
        {
            Name = "Settings",
            LaunchTarget = "ms-settings:",
            ProcessName = "SystemSettings",
            IconPath = File.Exists(settingsExe) ? settingsExe : Path.Combine(sys, "imageres.dll")
        });

        foreach (var a in apps) a.IconBitmap = IconHelper.GetAppIcon(a);
        return apps;
    }
}

/// <summary>
/// v1.1 icon path: IShellItemImageFactory at 256px (alpha preserved), AUMID
/// first when known, .lnk targets resolved. No letter tiles anywhere: the
/// only fallback is ShellIcons.GenericIcon, a neutral tile for apps whose
/// icon genuinely cannot be resolved.
/// </summary>
internal static class IconHelper
{
    public static Bitmap GetAppIcon(DockApp app)
    {
        // AUMID first when known: the Store app's real icon lives there, not
        // in its stub exe (calc.exe yields the old monochrome glyph).
        if (!string.IsNullOrWhiteSpace(app.AumidHint))
        {
            var fromAumid = ShellIcons.GetImageForParsingName("shell:AppsFolder\\" + app.AumidHint);
            if (fromAumid != null) return fromAumid;
        }

        string? path = app.IconPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    path = ShellIcons.ResolveLinkTarget(path);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var bmp = ShellIcons.GetImageForParsingName(path);
                    if (bmp != null) return bmp;
                }
            }
            catch { }
        }

        return ShellIcons.GenericIcon();
    }

    /// <summary>Display name for a process name (shared with the top bar).</summary>
    public static string FriendlyAppName(string processName)
    {
        return processName switch
        {
            "explorer" => "File Explorer",
            "Code" => "Visual Studio Code",
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "WindowsTerminal" => "Terminal",
            "CalculatorApp" => "Calculator",
            "SystemSettings" => "Settings",
            "notepad" => "Notepad",
            _ => processName
        };
    }
}
