using Windows.Media.Control;
using Windows.Storage.Streams;

namespace FrostDock;

/// <summary>
/// v1.4 Now Playing via Windows' Global System Media Transport Controls
/// (GSMTC) session manager. Reads ONLY the local playback session Windows
/// already exposes — no login, no API keys, no account access, no network.
/// Spotify desktop, Chrome/Edge media, etc. all surface through this API.
/// If WinRT interop is unavailable on a machine, Supported is false and
/// the Control Center falls back to media-key buttons (control without
/// track info) — track info is NEVER faked.
/// </summary>
internal sealed record NowPlayingInfo(string Title, string Artist, string AppName, bool IsPlaying, Bitmap? Art);

internal static class NowPlaying
{
    public static event EventHandler? Changed;

    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static GlobalSystemMediaTransportControlsSession? _session;
    private static bool _initialized;
    private static bool _supported = true;

    /// <summary>False when GSMTC interop failed on this machine (media-key fallback applies).</summary>
    public static bool Supported => _supported;

    public static async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => { AttachSession(_manager.GetCurrentSession()); };
            _manager.SessionsChanged += (_, _) => { AttachSession(_manager.GetCurrentSession()); };
            AttachSession(_manager.GetCurrentSession());
        }
        catch
        {
            _supported = false;
            _manager = null;
        }
    }

    private static void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            try { _session.MediaPropertiesChanged -= OnSessionChanged; } catch { }
            try { _session.PlaybackInfoChanged -= OnSessionChanged; } catch { }
        }
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void OnSessionChanged(GlobalSystemMediaTransportControlsSession _, object? __) =>
        Changed?.Invoke(null, EventArgs.Empty);

    public static async Task<NowPlayingInfo?> GetCurrentAsync()
    {
        if (!_supported) return null;
        var session = _manager?.GetCurrentSession() ?? _session;
        if (session == null) return null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            bool playing = playback?.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            string title = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? string.Empty : props.Artist;
            string app = FriendlyAppName(session.SourceAppUserModelId);
            Bitmap? art = null;
            try
            {
                if (props.Thumbnail != null)
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var net = stream.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await net.CopyToAsync(ms);
                    ms.Position = 0;
                    using var img = Image.FromStream(ms);
                    art = new Bitmap(img);
                }
            }
            catch { art = null; }
            return new NowPlayingInfo(title, artist, app, playing, art);
        }
        catch { return null; }
    }

    public static async Task<bool> PreviousAsync()
    {
        var session = _manager?.GetCurrentSession() ?? _session;
        if (session == null) return false;
        try { return await session.TrySkipPreviousAsync(); } catch { return false; }
    }

    public static async Task<bool> ToggleAsync()
    {
        var session = _manager?.GetCurrentSession() ?? _session;
        if (session == null) return false;
        try { return await session.TryTogglePlayPauseAsync(); } catch { return false; }
    }

    public static async Task<bool> NextAsync()
    {
        var session = _manager?.GetCurrentSession() ?? _session;
        if (session == null) return false;
        try { return await session.TrySkipNextAsync(); } catch { return false; }
    }

    /// <summary>
    /// Turns a raw AUMID ("SpotifyAB.SpotifyMusic_zpdnekdrzrea0") into a
    /// short display name ("Spotify"). Known desktop apps mapped explicitly;
    /// anything else falls back to the last dotted segment before '_'.
    /// </summary>
    private static string FriendlyAppName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "Media";
        if (aumid.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (aumid.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase) ||
            aumid.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (aumid.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (aumid.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (aumid.Contains("VLC", StringComparison.OrdinalIgnoreCase)) return "VLC";
        string s = aumid;
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return string.IsNullOrWhiteSpace(s) ? "Media" : s;
    }
}
