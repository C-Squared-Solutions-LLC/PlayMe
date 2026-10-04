using System.Runtime.InteropServices;

namespace PlayMe;

/// <summary>
/// Which processes belong to us. The player's audio is rendered by WebView2
/// child processes, not by PlayMe itself, so "the player's volume" means
/// "the volume of every process underneath this one" - and without this they
/// are indistinguishable from every other WebView2 app on the machine.
/// </summary>
public static class ProcessTree
{
    private static readonly int Own = Environment.ProcessId;
    private static Dictionary<int, int> _parents = new();
    private static DateTime _asOf = DateTime.MinValue;

    public static bool IsOursOrDescendant(int pid)
    {
        if (pid <= 0) return false;
        if (pid == Own) return true;
        var parents = Map();
        var current = pid;
        for (var hops = 0; hops < 24; hops++)
        {
            if (!parents.TryGetValue(current, out var parent) || parent <= 0) return false;
            if (parent == Own) return true;
            if (parent == current) return false;
            current = parent;
        }
        return false;
    }

    private static Dictionary<int, int> Map()
    {
        if (DateTime.UtcNow - _asOf < TimeSpan.FromSeconds(3) && _parents.Count > 0) return _parents;

        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
        {
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                    } while (Process32NextW(snapshot, ref entry));
                }
            }
            catch { }
            finally { CloseHandle(snapshot); }
        }

        if (parents.Count > 0)
        {
            _parents = parents;
            _asOf = DateTime.UtcNow;
        }
        return _parents;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
