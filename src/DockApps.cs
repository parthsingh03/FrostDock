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
    public Bitmap? IconBitmap { get; set; }

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
            IconPath = Path.Combine(sys, "calc.exe")
        });

        apps.Add(new DockApp
        {
            Name = "Settings",
            LaunchTarget = "ms-settings:",
            ProcessName = "SystemSettings",
            IconPath = Path.Combine(sys, "imageres.dll")
        });

        foreach (var a in apps) a.IconBitmap = IconHelper.GetAppIcon(a);
        return apps;
    }
}

internal static class IconHelper
{
    public static Bitmap? GetAppIcon(DockApp app)
    {
        string? path = app.IconPath;
        if (path != null && File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var bmp = icon.ToBitmap();
                    return RoundCorners(bmp, 96);
                }
            }
            catch { }
        }
        return GlyphIcon(app.Name);
    }

    /// <summary>Original fallback icon: gradient rounded square with the app's initial.</summary>
    public static Bitmap GlyphIcon(string name)
    {
        const int s = 96;
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(2, 2, s - 4, s - 4);
        using var path = RoundedRect(rect, 22);
        using var brush = new LinearGradientBrush(rect, Color.FromArgb(90, 170, 255), Color.FromArgb(40, 90, 220), 45f);
        g.FillPath(brush, path);
        string letter = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
        using var font = new Font("Segoe UI", 40f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(letter, font, Brushes.White, rect, sf);
        return bmp;
    }

    public static Bitmap RoundCorners(Bitmap src, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var rect = new Rectangle(0, 0, size, size);
        using var path = RoundedRect(rect, size / 4);
        g.SetClip(path);
        g.DrawImage(src, rect);
        return bmp;
    }

    public static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
