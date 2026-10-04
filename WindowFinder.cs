using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PlayMe;

public sealed record CaptureTarget(
    IntPtr Handle, string Title, string Process, int Pid, int Width, int Height, bool OnCurrentDesktop)
{
    public string Display => $"{Title} - {Process}";

    /// <summary>The site this window is showing, when the title names one.</summary>
    public (string Domain, string Name)? Site => IconProvider.SiteFor(Title);
}

/// <summary>
/// Lists the top-level windows that can be mirrored - including the ones
/// sitting on other virtual desktops, because those are exactly the ones you
/// want to keep an eye on - and re-finds a remembered one after a restart.
/// </summary>
public static class WindowFinder
{
    private static readonly string[] MediaApps =
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "vlc", "mpc-hc", "mpv",
          "amazon music", "spotify", "plex", "twitch", "discord" };

    /// <summary>
    /// Candidate windows, best first: whatever is making sound right now, then
    /// anything showing a site we recognise, then media apps.
    /// </summary>
    public static List<CaptureTarget> List(HashSet<int>? audiblePids = null)
    {
        var mine = Environment.ProcessId;
        var found = new List<CaptureTarget>();
        EnumWindows((h, _) =>
        {
            try
            {
                var target = Describe(h, mine);
                if (target is not null) found.Add(target);
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return found
            .OrderByDescending(t => Rank(t, audiblePids))
            .ThenByDescending(t => (long)t.Width * t.Height)
            .ToList();
    }

    private static int Rank(CaptureTarget target, HashSet<int>? audiblePids)
    {
        var rank = 0;
        if (audiblePids is not null && audiblePids.Contains(target.Pid)) rank += 4;
        if (target.Site is not null) rank += 2;
        if (MediaApps.Any(a => target.Process.Contains(a, StringComparison.OrdinalIgnoreCase))) rank += 1;
        return rank;
    }

    /// <summary>
    /// Re-find a remembered source after a restart. A browser retitles itself
    /// with whatever tab is in front, and several of its windows can be open at
    /// once, so the desktop the window was on is the strongest hint we have -
    /// without it PlayMe would just grab whichever window is in front of you.
    /// </summary>
    public static IntPtr Resolve(string? process, string? title, Guid? desktopId = null)
    {
        if (string.IsNullOrEmpty(process)) return IntPtr.Zero;
        var all = List().Where(t => t.Process.Equals(process, StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count == 0) return IntPtr.Zero;

        var onDesktop = desktopId is null
            ? all
            : all.Where(t => VirtualDesktops.DesktopIdOf(t.Handle) == desktopId).ToList();
        if (onDesktop.Count == 0) onDesktop = all;

        if (!string.IsNullOrEmpty(title))
        {
            var exact = onDesktop.FirstOrDefault(t =>
                t.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Handle;

            var head = title.Length > 12 ? title[..12] : title;
            var near = onDesktop.FirstOrDefault(t =>
                t.Title.StartsWith(head, StringComparison.OrdinalIgnoreCase));
            if (near is not null) return near.Handle;
        }
        return onDesktop.OrderByDescending(t => (long)t.Width * t.Height).First().Handle;
    }

    public static string TitleOf(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd);

    /// <summary>Describe one window, or null if it isn't worth offering.</summary>
    public static CaptureTarget? Describe(IntPtr hwnd) => Describe(hwnd, Environment.ProcessId);

    private static CaptureTarget? Describe(IntPtr hwnd, int myPid)
    {
        if (!IsWindowVisible(hwnd)) return null;
        if (GetWindowTextLength(hwnd) == 0) return null;

        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return null;

        // Cloaked means DWM isn't compositing it. Value 2 is the shell doing
        // that to a window on another virtual desktop - still a real window,
        // and the one you most want to watch. The other cloak flags are UWP
        // ghosts, which capture as blank.
        var onCurrentDesktop = true;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            if (cloaked != DWM_CLOAKED_SHELL) return null;
            onCurrentDesktop = false;
        }

        if (!GetWindowRect(hwnd, out var r)) return null;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w < 160 || h < 120) return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)myPid) return null;

        string proc;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            proc = p.ProcessName;
        }
        catch { return null; }

        var title = TitleOf(hwnd);
        if (title.Length == 0) return null;
        if (title is "Program Manager" or "Windows Input Experience" or "Windows Shell Experience Host")
            return null;

        return new CaptureTarget(hwnd, title, proc, (int)pid, w, h, onCurrentDesktop);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int DWM_CLOAKED_SHELL = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
