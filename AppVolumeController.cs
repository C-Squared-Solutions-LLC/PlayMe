using System.Diagnostics;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.CoreAudioApi;

namespace PlayMe;

/// <summary>
/// Per-app volume via WASAPI audio sessions on the default render device —
/// what the Windows volume mixer shows. A browser owns one mixer entry for
/// all its tabs, so this adjusts the whole app, matched by process name
/// (a browser may have several audio sessions; all are adjusted together).
///
/// Session objects are cached deliberately. Enumerating the mixer hands back
/// a COM wrapper per audio session, and each wrapper holds native state in
/// the audio service until it is released. The widget polls volume on a
/// timer, so re-enumerating (and dropping the wrappers on the floor) every
/// poll grew the audio service's memory without bound. Here every wrapper is
/// disposed the moment it is discarded, and the list is rebuilt only every
/// few seconds — or immediately when a cached session has expired.
/// </summary>
public sealed class AppVolumeController : IDisposable
{
    public sealed record VolumeState(bool Available, float Volume, bool Muted);

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeviceLifetime = TimeSpan.FromSeconds(30);

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private DateTime _deviceAsOf;
    private readonly Dictionary<uint, string> _pidNames = new();

    private readonly List<AudioSessionControl> _cached = new();
    private string? _cachedProcess;
    private DateTime _cacheAsOf;

    private MMDevice? Device()
    {
        // Re-resolve occasionally so a default-device change is picked up.
        if (_device is not null && DateTime.UtcNow - _deviceAsOf > DeviceLifetime) ResetDevice();
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            if (_device is null)
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceAsOf = DateTime.UtcNow;
            }
            return _device;
        }
        catch
        {
            ResetDevice();
            return null;
        }
    }

    private void ResetDevice()
    {
        DropCache();
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    private void DropCache()
    {
        foreach (var s in _cached)
        {
            try { s.Dispose(); } catch { }
        }
        _cached.Clear();
        _cachedProcess = null;
    }

    private string NameForPid(uint pid)
    {
        if (_pidNames.Count > 256) _pidNames.Clear();
        if (_pidNames.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch { name = ""; }
        _pidNames[pid] = name;
        return name;
    }

    private bool CacheUsable(string processName)
    {
        if (_cachedProcess != processName) return false;
        if (DateTime.UtcNow - _cacheAsOf > CacheLifetime) return false;
        foreach (var s in _cached)
        {
            try
            {
                if (s.State == AudioSessionState.AudioSessionStateExpired) return false;
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>
    /// The live sessions of <paramref name="processName"/>. The returned list
    /// is owned by this class — do not dispose or keep the entries.
    /// </summary>
    private List<AudioSessionControl> Sessions(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            DropCache();
            return _cached;
        }
        if (CacheUsable(processName)) return _cached;

        DropCache();
        _cachedProcess = processName;
        _cacheAsOf = DateTime.UtcNow;
        var device = Device();
        if (device is null) return _cached;
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var keep = false;
                try
                {
                    var pid = (int)s.GetProcessID;
                    // "PlayMe" means the player, whose audio is rendered by
                    // WebView2 children rather than by this process.
                    keep = processName.Equals("PlayMe", StringComparison.OrdinalIgnoreCase)
                        ? ProcessTree.IsOursOrDescendant(pid)
                        : NameForPid((uint)pid).Equals(processName, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                if (keep) _cached.Add(s);
                else s.Dispose(); // release every wrapper we are not keeping
            }
        }
        catch
        {
            DropCache();
            ResetDevice();
        }
        return _cached;
    }

    public VolumeState Get(string? processName)
    {
        var sessions = Sessions(processName);
        if (sessions.Count == 0) return new VolumeState(false, 0f, false);
        float volume = 0f;
        bool allMuted = true;
        bool any = false;
        foreach (var s in sessions)
        {
            try
            {
                var v = s.SimpleAudioVolume;
                volume = Math.Max(volume, v.Volume);
                allMuted &= v.Mute;
                any = true;
            }
            catch { DropCache(); return new VolumeState(false, 0f, false); }
        }
        return any ? new VolumeState(true, volume, allMuted) : new VolumeState(false, 0f, false);
    }

    public void SetVolume(string? processName, float volume)
    {
        var clamped = Math.Clamp(volume, 0f, 1f);
        foreach (var s in Sessions(processName))
        {
            try { s.SimpleAudioVolume.Volume = clamped; }
            catch { DropCache(); return; }
        }
    }

    public void SetMute(string? processName, bool mute)
    {
        foreach (var s in Sessions(processName))
        {
            try { s.SimpleAudioVolume.Mute = mute; }
            catch { DropCache(); return; }
        }
    }

    public void Dispose()
    {
        ResetDevice();
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }
}
