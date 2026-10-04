using System.Runtime.InteropServices;

namespace PlayMe;

/// <summary>
/// The bit of virtual-desktop state Windows actually exposes: whether a window
/// is on the desktop you are looking at right now.
/// </summary>
public static class VirtualDesktops
{
    private static IVirtualDesktopManager? _manager;
    private static bool _tried;

    private static IVirtualDesktopManager? Manager()
    {
        if (_tried) return _manager;
        _tried = true;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
            if (type is not null) _manager = (IVirtualDesktopManager)Activator.CreateInstance(type)!;
        }
        catch { _manager = null; }
        return _manager;
    }

    /// <summary>
    /// Which desktop a window lives on. Used to re-find the right window after
    /// a restart - titles move around, desktops don't.
    /// </summary>
    public static Guid? DesktopIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var manager = Manager();
        if (manager is null) return null;
        try
        {
            if (manager.GetWindowDesktopId(hwnd, out var id) != 0) return null;
            return id == Guid.Empty ? null : id;
        }
        catch { return null; }
    }

    /// <summary>null when Windows won't say (no virtual desktop support).</summary>
    public static bool? IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var manager = Manager();
        if (manager is null) return null;
        try
        {
            return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) == 0 ? on != 0 : null;
        }
        catch { return null; }
    }

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }
}
