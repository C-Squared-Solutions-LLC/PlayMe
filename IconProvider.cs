using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayMe;

/// <summary>
/// Icons for the things PlayMe lists: the app that owns a window or an audio
/// session, and - when a window title names a site - that site's favicon, so
/// streams can be picked out by their logo instead of by reading titles.
/// </summary>
public static class IconProvider
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SiteFetching = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string IconDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayMe", "icons");

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    /// <summary>Raised when a favicon finishes downloading, so menus can refresh.</summary>
    public static event Action? SiteIconArrived;

    // Sites worth recognising by name in a window title. The browser suffix is
    // stripped first, so "<video> - YouTube - Google Chrome" lands on youtube.
    private static readonly (string Needle, string Domain, string Name)[] Sites =
    {
        ("youtube", "youtube.com", "YouTube"),
        ("twitch", "twitch.tv", "Twitch"),
        ("netflix", "netflix.com", "Netflix"),
        ("prime video", "primevideo.com", "Prime Video"),
        ("disney+", "disneyplus.com", "Disney+"),
        ("hulu", "hulu.com", "Hulu"),
        ("kick.com", "kick.com", "Kick"),
        ("vimeo", "vimeo.com", "Vimeo"),
        ("crunchyroll", "crunchyroll.com", "Crunchyroll"),
        ("soundcloud", "soundcloud.com", "SoundCloud"),
        ("amazon music", "music.amazon.com", "Amazon Music"),
        ("spotify", "spotify.com", "Spotify"),
        ("plex", "plex.tv", "Plex"),
        ("tiktok", "tiktok.com", "TikTok"),
        ("instagram", "instagram.com", "Instagram"),
        ("facebook", "facebook.com", "Facebook"),
        ("reddit", "reddit.com", "Reddit"),
        ("twitter", "x.com", "X"),
    };

    private static readonly string[] BrowserSuffixes =
    {
        " - Google Chrome", " - Microsoft​ Edge", " - Microsoft Edge", " — Mozilla Firefox",
        " - Mozilla Firefox", " - Brave", " - Opera", " - Vivaldi", " and 1 more page - Personal",
    };

    /// <summary>The site a window title names, if it names one we know.</summary>
    public static (string Domain, string Name)? SiteFor(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title;
        foreach (var suffix in BrowserSuffixes)
        {
            var at = t.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (at > 0) { t = t[..at]; break; }
        }
        foreach (var (needle, domain, name) in Sites)
        {
            if (t.Contains(needle, StringComparison.OrdinalIgnoreCase)) return (domain, name);
        }
        return null;
    }

    /// <summary>
    /// Best icon for a window: the site's favicon when the title names a site,
    /// otherwise the owning app's icon.
    /// </summary>
    public static ImageSource? ForWindow(IntPtr hwnd, string? title, int pid)
    {
        var site = SiteFor(title);
        if (site is not null)
        {
            var icon = ForSite(site.Value.Domain);
            if (icon is not null) return icon;
        }
        return ForProcess(pid) ?? ForWindowHandle(hwnd);
    }

    public static ImageSource? ForProcess(int pid)
    {
        if (pid <= 0) return null;
        var path = ExePath(pid);
        return path is null ? null : ForExe(path);
    }

    public static ImageSource? ForExe(string exePath)
    {
        var key = "exe:" + exePath;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? image = null;
        var large = IntPtr.Zero;
        var small = IntPtr.Zero;
        try
        {
            if (ExtractIconEx(exePath, 0, out large, out small, 1) > 0)
            {
                var handle = large != IntPtr.Zero ? large : small;
                if (handle != IntPtr.Zero) image = FromHIcon(handle);
            }
        }
        catch { }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
        Cache[key] = image;
        return image;
    }

    private static ImageSource? ForWindowHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var handle = SendMessageW(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero);
            if (handle == IntPtr.Zero) handle = SendMessageW(hwnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero);
            if (handle == IntPtr.Zero) handle = GetClassLongPtrW(hwnd, GCLP_HICON);
            return handle == IntPtr.Zero ? null : FromHIcon(handle);
        }
        catch { return null; }
    }

    /// <summary>
    /// A site's favicon. Returns what is cached right now and kicks off a
    /// download if this is the first ask - <see cref="SiteIconArrived"/> fires
    /// when a later lookup will succeed.
    /// </summary>
    public static ImageSource? ForSite(string domain)
    {
        var key = "site:" + domain;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var file = Path.Combine(IconDir, domain.Replace('.', '_') + ".png");
        if (File.Exists(file))
        {
            var image = Load(file);
            Cache[key] = image;
            return image;
        }
        lock (SiteFetching)
        {
            if (!SiteFetching.Add(domain)) return null;
        }
        _ = Task.Run(() => FetchSiteAsync(domain, file));
        return null;
    }

    private static async Task FetchSiteAsync(string domain, string file)
    {
        try
        {
            Directory.CreateDirectory(IconDir);
            foreach (var url in new[]
                     {
                         $"https://{domain}/favicon.ico",
                         $"https://www.{domain}/favicon.ico",
                     })
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(url);
                    if (bytes.Length < 32) continue;
                    if (!SavePng(bytes, file)) continue;
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        Cache["site:" + domain] = Load(file);
                        SiteIconArrived?.Invoke();
                    });
                    return;
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            lock (SiteFetching) { SiteFetching.Remove(domain); }
        }
    }

    // Favicons are .ico containers; keep the biggest frame as a png so the
    // next start can just load it.
    private static bool SavePng(byte[] bytes, string file)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var frame in decoder.Frames)
            {
                if (best is null || frame.PixelWidth > best.PixelWidth) best = frame;
            }
            if (best is null) return false;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(best));
            using var output = File.Create(file);
            encoder.Save(output);
            return true;
        }
        catch { return false; }
    }

    private static ImageSource? Load(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = 32;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static ImageSource? FromHIcon(IntPtr handle)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static readonly Dictionary<int, string?> ExePaths = new();

    public static string? ExePath(int pid)
    {
        if (ExePaths.Count > 256) ExePaths.Clear();
        if (ExePaths.TryGetValue(pid, out var cached)) return cached;
        string? path = null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var builder = new StringBuilder(1024);
                var size = builder.Capacity;
                if (QueryFullProcessImageNameW(handle, 0, builder, ref size)) path = builder.ToString();
            }
            catch { }
            finally { CloseHandle(handle); }
        }
        ExePaths[pid] = path;
        return path;
    }

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A friendly app name: the exe's file description, else its name.</summary>
    public static string DescriptionForExe(string exePath)
    {
        if (Descriptions.TryGetValue(exePath, out var cached)) return cached;
        var name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName.Trim();
        }
        catch { }
        Descriptions[exePath] = name;
        return name;
    }

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int WM_GETICON = 0x007F;
    private const int GCLP_HICON = -14;
    private static readonly IntPtr ICON_BIG = new(1);
    private static readonly IntPtr ICON_SMALL2 = new(2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int index);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
