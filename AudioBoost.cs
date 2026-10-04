using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PlayMe;

/// <summary>
/// Plays one app louder than Windows allows.
///
/// Windows caps a mixer channel at 100%, so going past it means taking the
/// app's audio and rendering it again with gain. The process-loopback tap and
/// the speakers are attenuated identically by the app's own mixer level, so
/// the app is parked at <see cref="LeakVolume"/> - its direct output drops to
/// about -40 dB, inaudible - and the tap signal is scaled back up by exactly
/// that factor before being re-rendered. What you hear is the amplified copy;
/// the residual direct path is far too quiet to comb-filter against it.
///
/// Costs about 40 ms of delay, so it is worth leaving off for anything where
/// lip sync matters.
/// </summary>
public sealed class AudioBoost : IDisposable
{
    /// <summary>
    /// Where a boosted app's own mixer level is parked: about -34 dB, far
    /// enough under the amplified copy that the two never comb-filter.
    /// </summary>
    public const float LeakVolume = 0.02f;

    /// <summary>
    /// Undo what the leak level costs the tap. The tap is attenuated by the
    /// app's mixer level exactly linearly (measured), so this is its inverse.
    /// </summary>
    private const float LeakCompensation = 1f / LeakVolume;

    private const float LimitThreshold = 0.85f;

    private readonly object _gate = new();
    private ProcessLoopbackCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffered;
    private MMDevice? _device;
    private byte[] _scratch = Array.Empty<byte>();
    private int _maxBacklogBytes;
    private float _gain = 1f;

    public int ProcessId { get; private set; }
    public float Peak { get; private set; }
    public bool Active => _capture is not null;

    /// <summary>Why the last Start failed, for the UI to show.</summary>
    public string? LastError { get; private set; }

    public static bool IsSupported => ProcessLoopbackCapture.IsSupported;

    /// <summary>Gain relative to the app at 100%: 1.5 plays it half again as loud.</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 1f, 5f);
    }

    /// <summary>
    /// WASAPI activation hands back objects that cannot be used from a
    /// single-threaded apartment - the cast to IAudioClient fails outright
    /// with E_NOINTERFACE. The mixer lives on WPF's UI thread, which is STA,
    /// so every COM touch here is pushed onto a pool (MTA) thread.
    /// </summary>
    private static T InMta<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return work();
        return Task.Run(work).GetAwaiter().GetResult();
    }

    public bool Start(int processId, float gain) => InMta(() => StartCore(processId, gain));

    private bool StartCore(int processId, float gain)
    {
        Stop();
        if (!IsSupported) return false;
        Gain = gain;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var format = _device.AudioClient.MixFormat;

            _buffered = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(400),
            };
            _maxBacklogBytes = format.AverageBytesPerSecond / 10; // 100 ms, then resync

            _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 30);
            _output.Init(_buffered);
            _output.Play();

            _capture = new ProcessLoopbackCapture(format);
            _capture.DataAvailable += OnCaptured;
            if (!_capture.Start(processId))
            {
                LastError = _capture.LastError ?? "the per-app capture would not start";
                Stop();
                return false;
            }
            ProcessId = processId;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop() => InMta<object?>(() => { StopCore(); return null; });

    private void StopCore()
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnCaptured;
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
            _buffered = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
            ProcessId = 0;
            Peak = 0f;
        }
    }

    private void OnCaptured(byte[] buffer, int bytes)
    {
        var buffered = _buffered;
        if (buffered is null || bytes <= 0) return;

        // The tap runs ahead of the renderer if the machine stalls; drop the
        // backlog rather than let the delay creep up.
        if (buffered.BufferedBytes > _maxBacklogBytes)
        {
            try { buffered.ClearBuffer(); } catch { }
        }

        if (_scratch.Length < bytes) _scratch = new byte[bytes];
        var gain = _gain * LeakCompensation;
        var peak = 0f;

        for (var i = 0; i + 4 <= bytes; i += 4)
        {
            var sample = BitConverter.ToSingle(buffer, i) * gain;
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
            if (magnitude > LimitThreshold) sample = Limit(sample, magnitude);
            BitConverter.TryWriteBytes(_scratch.AsSpan(i, 4), sample);
        }

        Peak = Math.Min(peak, 1f);
        try { buffered.AddSamples(_scratch, 0, bytes); }
        catch { }
    }

    // Soft knee above the threshold so a boosted peak compresses instead of
    // slamming into the engine's hard clip.
    private static float Limit(float sample, float magnitude)
    {
        const float range = 1f - LimitThreshold;
        var over = (magnitude - LimitThreshold) / range;
        var shaped = LimitThreshold + range * MathF.Tanh(over);
        return sample < 0 ? -shaped : shaped;
    }

    public void Dispose() => Stop();
}
