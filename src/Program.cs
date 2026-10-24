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
        dock.ToggleTopBarRequested += (_, _) => topBar.Visible = !topBar.Visible;
        dock.ExitRequested += (_, _) =>
        {
            dock.RestoreTaskbar();
            Application.Exit();
        };

        dock.Show();
        topBar.Show();
        // Quick search stays hidden until the hotkey is pressed.

        Application.Run();
    }
}
