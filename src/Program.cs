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
        var dock = new DockForm(apps);
        var topBar = new TopBarForm();
        var quickSearch = new QuickSearchForm();
        var controlCenter = new ControlCenterForm();
        var launchpad = new LaunchpadForm();

        void ExitAll()
        {
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
            if (launchpad.Visible) launchpad.Hide();
            else launchpad.ShowLaunchpad();
        };
        topBar.ControlCenterRequested += (_, _) => controlCenter.TogglePanel();
        // Backstop for any other exit path: unregistering is idempotent.
        Application.ApplicationExit += (_, _) => topBar.UnregisterAppBar();

        dock.Show();
        topBar.Show();
        // Quick search, Control Center and Launchpad stay hidden until invoked.

        Application.Run();
    }
}
