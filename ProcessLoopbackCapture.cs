using System.Runtime.InteropServices;
using NAudio.Wave;

namespace PlayMe;

/// <summary>
/// Captures the audio of one process (and its children) through the Windows
/// process-loopback tap - the same mechanism Game Bar uses to record a single
/// app. Windows 10 build 20348+ / Windows 11 only.
///
/// Frames arrive in the requested format, on a capture thread.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualDevice = "VAD\\Process_Loopback";
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint BufferFlagsSilent = 0x2;
    private const uint WaitTimeout = 0x102;

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private IntPtr _event;
    private Thread? _thread;
    private volatile bool _stop;
    private byte[] _buffer = Array.Empty<byte>();

    public WaveFormat Format { get; }

    /// <summary>Why the last Start failed.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raw frames. The buffer is reused, so copy anything you keep.</summary>
    public event Action<byte[], int>? DataAvailable;

    public bool Running => _thread is not null;

    public ProcessLoopbackCapture(WaveFormat format) => Format = format;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public bool Start(int processId)
    {
        Stop();
        if (!IsSupported) return false;
        try
        {
            // Activation completes on a pool (MTA) thread, so run the whole
            // dance off the UI thread - waiting on an STA pump would hang.
            var task = Task.Run(() => Activate(processId));
            if (!task.Wait(4000) || task.Result is null) return false;
            _client = task.Result;

            var fmt = WaveFormat.MarshalToPtr(Format);
            try
            {
                var hr = _client.Initialize(0, StreamFlagsLoopback | StreamFlagsEventCallback,
                    2_000_000, 0, fmt, IntPtr.Zero);
                if (hr != 0) { Cleanup(); return false; }
            }
            finally { Marshal.FreeHGlobal(fmt); }

            _event = CreateEventW(IntPtr.Zero, false, false, null);
            if (_client.SetEventHandle(_event) != 0) { Cleanup(); return false; }

            var iid = IID_IAudioCaptureClient;
            if (_client.GetService(ref iid, out var service) != 0) { Cleanup(); return false; }
            _capture = (IAudioCaptureClient)service;

            if (_client.Start() != 0) { Cleanup(); return false; }

            _stop = false;
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "PlayMe loopback",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Cleanup();
            return false;
        }
    }

    public void Stop()
    {
        _stop = true;
        if (_event != IntPtr.Zero) SetEvent(_event);
        var t = _thread;
        _thread = null;
        try { t?.Join(600); } catch { }
        Cleanup();
    }

    private void Cleanup()
    {
        try { _client?.Stop(); } catch { }
        if (_capture is not null) { try { Marshal.ReleaseComObject(_capture); } catch { } }
        if (_client is not null) { try { Marshal.ReleaseComObject(_client); } catch { } }
        _capture = null;
        _client = null;
        if (_event != IntPtr.Zero)
        {
            CloseHandle(_event);
            _event = IntPtr.Zero;
        }
    }

    private void Pump()
    {
        while (!_stop)
        {
            if (WaitForSingleObject(_event, 200) == WaitTimeout) continue;
            if (_stop) break;
            try
            {
                while (_capture is not null && _capture.GetNextPacketSize(out var next) == 0 && next > 0)
                {
                    if (_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) break;
                    var bytes = (int)frames * Format.BlockAlign;
                    if (bytes > 0)
                    {
                        if (_buffer.Length < bytes) _buffer = new byte[bytes];
                        if ((flags & BufferFlagsSilent) != 0) Array.Clear(_buffer, 0, bytes);
                        else Marshal.Copy(data, _buffer, 0, bytes);
                    }
                    _capture.ReleaseBuffer(frames);
                    if (bytes > 0) DataAvailable?.Invoke(_buffer, bytes);
                }
            }
            catch { break; }
        }
    }

    private IAudioClient? Activate(int processId)
    {
        var activation = new ActivationParams
        {
            ActivationType = 1,       // process loopback
            TargetProcessId = (uint)processId,
            LoopbackMode = 0,         // include the target process tree
        };
        var size = Marshal.SizeOf<ActivationParams>();
        var blob = Marshal.AllocHGlobal(size);
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activation, blob, false);
            Marshal.StructureToPtr(
                new PropVariantBlob { Vt = 65, Size = (uint)size, Data = blob }, variant, false);

            var handler = new ActivationHandler();
            ActivateAudioInterfaceAsync(VirtualDevice, IID_IAudioClient, variant, handler, out _);
            if (!handler.Done.Wait(3000) || handler.Operation is null) return null;
            handler.Operation.GetActivateResult(out var hr, out var iface);
            return hr == 0 ? iface as IAudioClient : null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Dispose() => Stop();

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public IActivateAudioInterfaceAsyncOperation? Operation;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Operation = operation;
            Done.Set();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int activateResult,
                               [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
                                     long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid interfaceId,
                                     [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                                    out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string devicePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
