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
        dock.ExitRequested += (_, _) =>
        {
            dock.RestoreTaskbar();
            topBar.UnregisterAppBar();
            Application.Exit();
        };
        // Backstop for any other exit path: unregistering is idempotent.
        Application.ApplicationExit += (_, _) => topBar.UnregisterAppBar();

        dock.Show();
        topBar.Show();
        // Quick search stays hidden until the hotkey is pressed.

        Application.Run();
    }
}
