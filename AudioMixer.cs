using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PlayMe;

/// <summary>
/// Everything the Windows volume mixer shows: the output device plus one
/// channel per app that holds an audio session, with live peak levels.
///
/// Session wrappers are owned here and disposed on every rebuild - see
/// AppVolumeController for why that matters.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public sealed class Channel
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required int ProcessId { get; init; }
        public string? ExePath { get; init; }
        public bool IsSystemSounds { get; init; }
        internal List<AudioSessionControl> Sessions { get; } = new();
    }

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private readonly List<Channel> _channels = new();
    private readonly int _ownProcessId = Environment.ProcessId;

    public IReadOnlyList<Channel> Channels => _channels;

    /// <summary>The output device, or null when it can't be reached.</summary>
    private MMDevice? Device()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
        DropChannels();
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    private void DropChannels()
    {
        foreach (var channel in _channels)
        {
            foreach (var session in channel.Sessions)
            {
                try { session.Dispose(); } catch { }
            }
        }
        _channels.Clear();
    }

    /// <summary>Rebuild the channel list. Call this on a slow timer, not per frame.</summary>
    public void Refresh()
    {
        DropChannels();
        var device = Device();
        if (device is null) return;
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            var byKey = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var keep = false;
                try
                {
                    var state = session.State;
                    if (state == AudioSessionState.AudioSessionStateExpired)
                    {
                        session.Dispose();
                        continue;
                    }

                    var pid = (int)session.GetProcessID;
                    string key, name;
                    string? exe = null;
                    var systemSounds = session.IsSystemSoundsSession;
                    if (systemSounds)
                    {
                        key = "@system";
                        name = "System sounds";
                    }
                    else if (ProcessTree.IsOursOrDescendant(pid))
                    {
                        // The player's sound comes out of WebView2 child
                        // processes; grouped here so it reads as one app
                        // instead of hiding among every other WebView2.
                        key = "@playme";
                        name = "PlayMe (player)";
                        exe = IconProvider.ExePath(_ownProcessId);
                    }
                    else
                    {
                        exe = IconProvider.ExePath(pid);
                        key = exe ?? ("pid:" + pid);
                        name = exe is not null
                            ? IconProvider.DescriptionForExe(exe)
                            : Describe(session, pid);
                    }

                    if (!byKey.TryGetValue(key, out var channel))
                    {
                        channel = new Channel
                        {
                            Key = key,
                            Name = name,
                            ProcessId = pid,
                            ExePath = exe,
                            IsSystemSounds = systemSounds,
                        };
                        byKey[key] = channel;
                        _channels.Add(channel);
                    }
                    channel.Sessions.Add(session);
                    keep = true;
                }
                catch { }
                if (!keep)
                {
                    try { session.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            DropChannels();
            ResetDevice();
        }
    }

    private static string Describe(AudioSessionControl session, int pid)
    {
        try
        {
            var display = session.DisplayName;
            if (!string.IsNullOrWhiteSpace(display)) return display;
        }
        catch { }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch { return "pid " + pid; }
    }

    public float VolumeOf(Channel channel)
    {
        foreach (var session in channel.Sessions)
        {
            try { return session.SimpleAudioVolume.Volume; }
            catch { }
        }
        return 0f;
    }

    public bool MutedOf(Channel channel)
    {
        foreach (var session in channel.Sessions)
        {
            try { return session.SimpleAudioVolume.Mute; }
            catch { }
        }
        return false;
    }

    public float PeakOf(Channel channel)
    {
        var peak = 0f;
        foreach (var session in channel.Sessions)
        {
            try { peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue); }
            catch { }
        }
        return peak;
    }

    public void SetVolume(Channel channel, float volume)
    {
        var clamped = Math.Clamp(volume, 0f, 1f);
        foreach (var session in channel.Sessions)
        {
            try { session.SimpleAudioVolume.Volume = clamped; }
            catch { }
        }
    }

    public void SetMute(Channel channel, bool mute)
    {
        foreach (var session in channel.Sessions)
        {
            try { session.SimpleAudioVolume.Mute = mute; }
            catch { }
        }
    }

    public float MasterVolume
    {
        get
        {
            try { return Device()?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
            catch { return 0f; }
        }
        set
        {
            try
            {
                var device = Device();
                if (device is not null)
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            }
            catch { }
        }
    }

    public bool MasterMuted
    {
        get
        {
            try { return Device()?.AudioEndpointVolume.Mute ?? false; }
            catch { return false; }
        }
        set
        {
            try
            {
                var device = Device();
                if (device is not null) device.AudioEndpointVolume.Mute = value;
            }
            catch { }
        }
    }

    public float MasterPeak
    {
        get
        {
            try { return Device()?.AudioMeterInformation.MasterPeakValue ?? 0f; }
            catch { return 0f; }
        }
    }

    public string MasterName
    {
        get
        {
            try
            {
                var name = Device()?.FriendlyName;
                return string.IsNullOrWhiteSpace(name) ? "Speakers" : name;
            }
            catch { return "Speakers"; }
        }
    }

    /// <summary>Process ids that currently hold a playing audio session.</summary>
    public HashSet<int> AudiblePids()
    {
        var pids = new HashSet<int>();
        foreach (var channel in _channels)
        {
            foreach (var session in channel.Sessions)
            {
                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateActive)
                        pids.Add((int)session.GetProcessID);
                }
                catch { }
            }
        }
        return pids;
    }

    public void Dispose()
    {
        ResetDevice();
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }
}
