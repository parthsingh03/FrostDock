namespace FrostDock;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "FrostDock_SingleInstance", out bool isNew);
        if (!isNew) return; // already running

        ApplicationConfiguration.Initialize();

        var apps = DockAppDiscovery.DefaultApps();
        // v1.4: user pins from %LocalAppData%\FrostDock\pinned.json merge
        // AFTER the built-in defaults, deduped by target path or name.
        var userPinPaths = new List<string>();
        foreach (var pin in PinnedStore.Load())
        {
            bool dup = false;
            foreach (var a in apps)
            {
                if (a.LaunchTarget.Equals(pin.Path, StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Equals(pin.Name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            }
            if (dup) continue;
            apps.Add(DockForm.DockAppFromPin(pin.Name, pin.Path));
            userPinPaths.Add(pin.Path);
        }
        var dock = new DockForm(apps);
        dock.SetUserPinPaths(userPinPaths);
        var topBar = new TopBarForm();
        var quickSearch = new QuickSearchForm();
        var controlCenter = new ControlCenterForm();
        var launchpad = new LaunchpadForm();

        // v1.4: GSMTC session tracking starts at launch (Control Center
        // refreshes from it on open and on session/track changes).
        _ = NowPlaying.InitializeAsync();

        // v1.4 wiring: drag-to-pin, drop target, zoom origin, bounce hints.
        launchpad.PinDropped += (_, item) => dock.PinApp(item);
        launchpad.GetDockRect = () => dock.PillScreenRect;
        // v1.5 wiring: during a drag the dock comes above Launchpad (so the
        // drop target is visible) and its pill highlights on hover.
        launchpad.DragRevealDockRequested += (_, _) => dock.BringToFrontNoActivate();
        launchpad.SetDockDropHighlight = on => dock.SetDropHighlight(on);
        launchpad.AppLaunched += (_, name) => dock.RequestBounce(name);
        quickSearch.AppLaunched += (_, name) => dock.RequestBounce(name);

        void ExitAll()
        {
            // v1.5 backstop: clear any genie transition-suppression flags so
            // a clean exit never leaves another window's animations off.
            GenieEffect.RestoreAllTransitions();
            dock.RestoreTaskbar();
            topBar.UnregisterAppBar();
            Application.Exit();
        }

        dock.ShowSearchRequested += (_, _) =>
        {
            if (quickSearch.Visible) quickSearch.Hide();
            else quickSearch.ShowSearch();
        };
        // Hiding the top bar must also release its AppBar workspace
        // reservation, and showing it must reserve it again.
        dock.ToggleTopBarRequested += (_, _) =>
        {
            if (topBar.Visible) topBar.HideBar();
            else topBar.ShowBar();
        };
        dock.ExitRequested += (_, _) => ExitAll();
        topBar.ExitRequested += (_, _) => ExitAll();
        dock.LaunchpadRequested += (_, _) =>
        {
            if (launchpad.Visible) launchpad.HideLaunchpad();
            else
            {
                launchpad.ZoomOriginScreen = dock.LaunchpadIconScreenCenter;
                launchpad.ShowLaunchpad();
            }
        };
        topBar.ControlCenterRequested += (_, _) => controlCenter.TogglePanel();
        // Backstop for any other exit path: unregistering is idempotent.
        Application.ApplicationExit += (_, _) =>
        {
            topBar.UnregisterAppBar();
            GenieEffect.RestoreAllTransitions();
        };

        dock.Show();
        topBar.Show();
        // Quick search, Control Center and Launchpad stay hidden until invoked.

        Application.Run();
    }
}
