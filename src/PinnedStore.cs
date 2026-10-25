using System.Text.Json;

namespace FrostDock;

/// <summary>
/// v1.4 user pins for drag-from-Launchpad-to-dock. Stored as small JSON
/// under %LocalAppData%\FrostDock\pinned.json: name, launch target (exe/
/// .lnk path or Store AUMID via "shell:AppsFolder\…" path), icon source
/// path/AUMID, and process name. Missing or corrupt file means defaults
/// only — never a crash, never a fake pin.
/// </summary>
internal sealed record PinnedEntry(string Name, string Path, string IconSource, string ProcessName);

internal static class PinnedStore
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrostDock", "pinned.json");

    public static List<PinnedEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<PinnedEntry>();
            string json = File.ReadAllText(FilePath);
            var entries = JsonSerializer.Deserialize<List<PinnedEntry>>(json);
            return entries?.Where(e => !string.IsNullOrWhiteSpace(e.Name) &&
                                       !string.IsNullOrWhiteSpace(e.Path)).ToList()
                   ?? new List<PinnedEntry>();
        }
        catch { return new List<PinnedEntry>(); }
    }

    public static void Save(List<PinnedEntry> entries)
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public static void Add(PinnedEntry entry)
    {
        var entries = Load();
        entries.RemoveAll(e => e.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) ||
                               e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
        entries.Add(entry);
        Save(entries);
    }

    public static void Remove(string path)
    {
        var entries = Load();
        if (entries.RemoveAll(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0)
            Save(entries);
    }

    public static bool Contains(string path) =>
        Load().Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
}
