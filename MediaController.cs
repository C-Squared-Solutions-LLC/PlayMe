using System.IO;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace PlayMe;

public sealed record TrackInfo(string Title, string Artist, byte[]? ArtBytes);
public sealed record PlaybackState(bool HasSession, bool IsPlaying);
public sealed record TimelineState(TimeSpan Position, TimeSpan Duration, DateTimeOffset AsOf);
public sealed record StreamInfo(int Index, string AppId, string Title, string Artist, bool IsPlaying, bool IsSelected);

/// <summary>
/// Wraps the Windows system media session (SMTC) — the same channel the
/// hardware media keys use. Tracks ALL sessions that match the configured
/// source filter, auto-picks one to control (playing first), and lets the
/// user pin a specific stream. Windows does not expose the page URL of a
/// browser stream, so domain-style source entries match any browser session;
/// a browser reports one stream — its active media tab.
/// Events may fire on non-UI threads; callers marshal to their dispatcher.
/// </summary>
public sealed class MediaController
{
    private sealed class Entry
    {
        public required GlobalSystemMediaTransportControlsSession Session;
        public required string AppId;
        public string Title = "";
        public string Artist = "";
        public bool Playing;
    }

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly object _gate = new();
    private List<Entry> _entries = new();
    private GlobalSystemMediaTransportControlsSession? _active;
    private GlobalSystemMediaTransportControlsSession? _pinned;
    private string _pinnedAppId = "";
    private string _pinnedTitle = "";
    private readonly List<GlobalSystemMediaTransportControlsSession> _monitored = new();
    private int _rebuildQueued;
    private string? _lastSignature;
    private string _artKey = "";
    private byte[]? _artCache;

    public event Action? MediaChanged;
    public event Action? PlaybackChanged;
    public event Action? TimelineChanged;
    public event Action? StreamsChanged;

    public string? ActiveAppId
    {
        get
        {
            lock (_gate)
                return _entries.FirstOrDefault(e => ReferenceEquals(e.Session, _active))?.AppId;
        }
    }

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => RequestRebuild();
        _manager.SessionsChanged += (_, _) => RequestRebuild();
        await RebuildAsync();
    }

    // Players can fire metadata/playback events in bursts (a browser flipping
    // tabs, a live stream rewriting its title). Kicking off a rebuild per
    // event let the work queue grow without bound and, through the events a
    // rebuild raises, dragged album-art reads along with it. One rebuild runs
    // at a time with at most one more queued behind it, after a short settle.
    private void RequestRebuild()
    {
        if (Interlocked.Exchange(ref _rebuildQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            Interlocked.Exchange(ref _rebuildQueued, 0);
            try { await RebuildAsync(); } catch { }
        });
    }


    public static string? ProcessNameForAppId(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "msedge";
        if (appId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (appId.Contains("308046B", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (appId.Contains("opera", StringComparison.OrdinalIgnoreCase)) return "opera";
        if (appId.Contains("brave", StringComparison.OrdinalIgnoreCase)) return "brave";
        if (appId.Contains("vivaldi", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
        // The player's media comes through WebView2. Resolving it to "PlayMe"
        // is safe either way: the volume lookup for that name only ever matches
        // our own child processes, so another app's WebView2 simply misses.
        if (appId.Contains("playme", StringComparison.OrdinalIgnoreCase)) return "PlayMe";
        if (appId.Contains("webview2", StringComparison.OrdinalIgnoreCase)) return "PlayMe";
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return "Amazon Music";
        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(appId);
        return null;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return false; }
    }

    private async Task RebuildAsync()
    {
        if (_manager is null) return;
        await _rebuildGate.WaitAsync();
        try
        {
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = _manager.GetSessions(); }
            catch { sessions = Array.Empty<GlobalSystemMediaTransportControlsSession>(); }

            // Every session Windows reports is offered; you pick which one to
            // control from the widget. (PlayMe used to filter by a source
            // allow-list, which existed only because it had no say in what the
            // browser was playing - it opens sites itself now.)
            var list = new List<Entry>();
            foreach (var s in sessions)
            {
                string appId = "";
                try { appId = s.SourceAppUserModelId ?? ""; } catch { }

                var entry = new Entry { Session = s, AppId = appId, Playing = IsPlaying(s) };
                try
                {
                    var p = await s.TryGetMediaPropertiesAsync();
                    entry.Title = p.Title ?? "";
                    entry.Artist = p.Artist ?? "";
                }
                catch { }
                list.Add(entry);
            }

            string signature;
            lock (_gate)
            {
                _entries = list;
                MonitorSessions(sessions);

                if (_pinned is not null)
                {
                    // Re-find the pinned stream: same object, else its app if
                    // unambiguous (title changes track-to-track), else title.
                    var match = list.FirstOrDefault(e => ReferenceEquals(e.Session, _pinned));
                    if (match is null)
                    {
                        var sameApp = list.Where(e => e.AppId == _pinnedAppId).ToList();
                        match = sameApp.Count == 1
                            ? sameApp[0]
                            : sameApp.FirstOrDefault(e => e.Title == _pinnedTitle);
                    }
                    if (match is null)
                    {
                        _pinned = null;
                    }
                    else
                    {
                        _pinned = match.Session;
                        _pinnedTitle = match.Title;
                    }
                }

                var activeEntry =
                    (_pinned is not null ? list.FirstOrDefault(e => ReferenceEquals(e.Session, _pinned)) : null)
                    ?? list.FirstOrDefault(e => e.Playing)
                    ?? list.FirstOrDefault();
                SwapActive(activeEntry?.Session);
                signature = Signature(list, activeEntry);
            }

            // Nothing the UI shows actually moved — don't wake it up. This is
            // what keeps repeated no-op events from re-reading album art.
            if (signature == _lastSignature) return;
            _lastSignature = signature;

            StreamsChanged?.Invoke();
            MediaChanged?.Invoke();
            PlaybackChanged?.Invoke();
            TimelineChanged?.Invoke();
        }
        finally { _rebuildGate.Release(); }
    }

    private static string Signature(List<Entry> list, Entry? active)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in list) sb.Append(e.AppId).Append('|').Append(e.Title).Append('|')
                                 .Append(e.Artist).Append('|').Append(e.Playing).Append(';');
        sb.Append('#').Append(active?.AppId).Append('|').Append(active?.Title);
        return sb.ToString();
    }

    // Callers hold _gate. Media/playback changes are watched on ALL sessions
    // (see MonitorSessions); the active session only adds timeline tracking.
    private void SwapActive(GlobalSystemMediaTransportControlsSession? next)
    {
        if (ReferenceEquals(_active, next)) return;
        if (_active is not null)
        {
            try { _active.TimelinePropertiesChanged -= OnTimeline; } catch { }
        }
        _active = next;
        if (_active is not null)
        {
            _active.TimelinePropertiesChanged += OnTimeline;
        }
    }

    // Callers hold _gate. Watch every session INCLUDING filtered-out ones: a
    // browser stream flipping back from Twitch to music is the same session
    // object with new metadata - no SessionsChanged fires. Without this the
    // widget went deaf once every session was filtered and never recovered.
    private void MonitorSessions(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        foreach (var m in _monitored)
        {
            try
            {
                m.MediaPropertiesChanged -= OnAnyMediaProps;
                m.PlaybackInfoChanged -= OnAnyPlayback;
            }
            catch { }
        }
        _monitored.Clear();
        foreach (var s in sessions)
        {
            try
            {
                s.MediaPropertiesChanged += OnAnyMediaProps;
                s.PlaybackInfoChanged += OnAnyPlayback;
                _monitored.Add(s);
            }
            catch { }
        }
    }

    private void OnAnyMediaProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) =>
        RequestRebuild();

    private void OnAnyPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) =>
        RequestRebuild();

    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) =>
        TimelineChanged?.Invoke();

    public IReadOnlyList<StreamInfo> GetStreams()
    {
        lock (_gate)
        {
            return _entries
                .Select((e, i) => new StreamInfo(i, e.AppId, e.Title, e.Artist, e.Playing,
                                                 ReferenceEquals(e.Session, _active)))
                .ToList();
        }
    }

    public void SelectStream(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _entries.Count) return;
            var e = _entries[index];
            _pinned = e.Session;
            _pinnedAppId = e.AppId;
            _pinnedTitle = e.Title;
            SwapActive(e.Session);
        }
        StreamsChanged?.Invoke();
        MediaChanged?.Invoke();
        PlaybackChanged?.Invoke();
        TimelineChanged?.Invoke();
    }

    private GlobalSystemMediaTransportControlsSession? Active
    {
        get { lock (_gate) return _active; }
    }

    public async Task<TrackInfo?> GetTrackAsync()
    {
        var s = Active;
        if (s is null) return null;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            var title = p.Title ?? "";
            var artist = p.Artist ?? "";

            // Album art is by far the biggest allocation here — hundreds of KB
            // that land on the large object heap. Read it once per track and
            // hand the same array back on every later refresh of that track.
            var key = (ActiveAppId ?? "") + "|" + title + "|" + artist;
            if (key == _artKey) return new TrackInfo(title, artist, _artCache);

            byte[]? art = null;
            if (p.Thumbnail is not null)
            {
                using var ras = await p.Thumbnail.OpenReadAsync();
                using var reader = new DataReader(ras);
                if (ras.Size > 0)
                {
                    var size = (uint)Math.Min(ras.Size, 8_000_000);
                    await reader.LoadAsync(size);
                    art = new byte[size];
                    reader.ReadBytes(art);
                }
                else
                {
                    // Some players report Size=0 on a stream that still has
                    // data; read in chunks until exhausted.
                    var chunks = new List<byte[]>();
                    var total = 0;
                    while (total < 8_000_000)
                    {
                        var loaded = await reader.LoadAsync(65536);
                        if (loaded == 0) break;
                        var buf = new byte[loaded];
                        reader.ReadBytes(buf);
                        chunks.Add(buf);
                        total += (int)loaded;
                    }
                    if (total > 0)
                    {
                        art = new byte[total];
                        var off = 0;
                        foreach (var c in chunks) { System.Buffer.BlockCopy(c, 0, art, off, c.Length); off += c.Length; }
                    }
                }
            }
            _artKey = key;
            _artCache = art;
            return new TrackInfo(title, artist, art);
        }
        catch { return null; }
    }

    public PlaybackState GetPlayback()
    {
        var s = Active;
        if (s is null) return new PlaybackState(false, false);
        return new PlaybackState(true, IsPlaying(s));
    }

    public TimelineState GetTimeline()
    {
        var s = Active;
        if (s is not null)
        {
            try
            {
                var t = s.GetTimelineProperties();
                return new TimelineState(t.Position - t.StartTime, t.EndTime - t.StartTime, t.LastUpdatedTime);
            }
            catch { }
        }
        return new TimelineState(TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.UtcNow);
    }

    public async void TogglePlayPause()
    {
        try { if (Active is { } s) await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { if (Active is { } s) await s.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        try { if (Active is { } s) await s.TrySkipPreviousAsync(); } catch { }
    }

    public async void SeekTo(TimeSpan position)
    {
        try { if (Active is { } s) await s.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }
}
