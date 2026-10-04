using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PlayMe;

public partial class MainWindow : Window
{
    private readonly MediaController _media = new();
    private readonly AppVolumeController _volume = new();
    private readonly Settings _settings;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _topmostTick = new() { Interval = TimeSpan.FromSeconds(2) };

    private TimeSpan _basePosition;
    private TimeSpan _duration;
    private DateTimeOffset _baseAsOf = DateTimeOffset.UtcNow;
    private bool _isPlaying;
    private bool _muted;
    private bool _volUpdatingUI;
    private int _tickCount;
    private bool _ducked;
    private float _preDuckVolume;
    private string? _duckedProcess;
    private bool _menuOpen;
    private string _lastTitle = "";
    private BitmapImage? _lastArt;
    private byte[]? _lastArtBytes;
    private readonly ArtFetcher _artFetcher = new();
    private PlayerWindow? _player;
    private MixerWindow? _mixer;
    private double _prevLeft, _prevTop;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        if (_settings.Left is double l && _settings.Top is double t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            PositionBottomRight();
        }
        Topmost = _settings.Topmost;
        TopmostMenuItem.IsChecked = _settings.Topmost;
        QuietButton.ToolTip = $"Quiet ({Math.Round(_settings.QuietLevel * 100)}%)";
        Card.ContextMenu!.Opened += (_, _) => _menuOpen = true;
        Card.ContextMenu!.Closed += (_, _) => _menuOpen = false;

        _prevLeft = Left;
        _prevTop = Top;

        Loaded += async (_, _) => await InitAsync();
        LocationChanged += (_, _) =>
        {
            DragVideoAlong();
            _saveDebounce.Stop();
            _saveDebounce.Start();
        };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveSettings(); };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        _topmostTick.Tick += (_, _) => ReassertTopmost();
        _topmostTick.Start();
    }

    // Tool-window style keeps the widget out of Alt-Tab/Task View AND outside
    // virtual-desktop management, so it shows on every desktop. No-activate
    // means clicking its buttons never steals focus from the app you're in.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    // WPF's Topmost flag can be knocked off by fullscreen apps and other
    // topmost-window churn; reassert the z-order position periodically.
    // Paused while a menu is open: reasserting would lift the widget above
    // its own popup and the menu would land behind the card.
    private void ReassertTopmost()
    {
        if (!Topmost || _menuOpen) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private async Task InitAsync()
    {
        _media.MediaChanged += () => Dispatcher.InvokeAsync(() => _ = RefreshTrackAsync());
        _media.PlaybackChanged += () => Dispatcher.InvokeAsync(RefreshPlayback);
        _media.TimelineChanged += () => Dispatcher.InvokeAsync(RefreshTimeline);
        await _media.InitializeAsync();
        await RefreshTrackAsync();
        RefreshPlayback();
        RefreshTimeline();
        RefreshVolume();
        RestoreVideo();

        // If PlayMe died while an app was boosted, that app is still parked at
        // the boost leak level. Put it back before anything else touches it.
        MixerWindow.RestoreAfterCrash(_settings, _volume);
        if (_settings.MixerEnabled) ShowMixer();
    }

    // ---- mixer --------------------------------------------------------

    private MixerWindow EnsureMixer()
    {
        if (_mixer is not null) return _mixer;
        var m = new MixerWindow(_settings);
        m.SettingsChanged += () => SettingsStore.Save(_settings);
        m.IsVisibleChanged += (_, _) =>
        {
            _settings.MixerEnabled = m.IsVisible;
            MixerGlyph.Foreground = (System.Windows.Media.Brush)
                FindResource(m.IsVisible ? "Accent" : "TextSecondary");
            SettingsStore.Save(_settings);
        };
        _mixer = m;
        return m;
    }

    private void ShowMixer()
    {
        var m = EnsureMixer();
        if (_settings.MixerLeft is double l && _settings.MixerTop is double t && IsOnScreen(l, t))
        {
            m.PlaceAt(l, t);
        }
        else
        {
            // Park it above whatever is already there - the video panel when
            // it's up, otherwise the widget. Re-asked as those move.
            m.Left = Left - 8;
            m.AnchorBottom = () => (_player is { IsVisible: true } ? _player.Top : Top) - 6;
        }
        m.Show();
    }

    private void MixerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mixer is { IsVisible: true })
        {
            _mixer.Hide();
            return;
        }
        ShowMixer();
    }

    private void OnTick()
    {
        UpdateProgress();
        if (++_tickCount % 4 == 0) RefreshVolume(); // mixer poll: once a second
    }

    // ---- player -------------------------------------------------------

    private PlayerWindow EnsurePlayer()
    {
        if (_player is not null) return _player;
        var p = new PlayerWindow(_settings);
        p.SettingsChanged += () => SettingsStore.Save(_settings);
        p.IsVisibleChanged += (_, _) =>
        {
            _settings.VideoEnabled = p.IsVisible;
            VideoGlyph.Foreground = (System.Windows.Media.Brush)
                FindResource(p.IsVisible ? "Accent" : "TextSecondary");
            SettingsStore.Save(_settings);
        };
        _player = p;
        return p;
    }

    private void PositionPlayer(PlayerWindow p)
    {
        if (_settings.VideoLeft is double l && _settings.VideoTop is double t && IsOnScreen(l, t))
        {
            p.Left = l;
            p.Top = t;
        }
        else
        {
            // Default berth: sitting on top of the widget.
            p.Left = Left;
            p.Top = Math.Max(SystemParameters.VirtualScreenTop, Top - p.Height - 6);
        }
    }

    // Keep the panel pinned to the widget as the widget is dragged around.
    private void DragVideoAlong()
    {
        var dx = Left - _prevLeft;
        var dy = Top - _prevTop;
        _prevLeft = Left;
        _prevTop = Top;
        if (!_settings.VideoFollowsWidget || _player is not { IsVisible: true }) return;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
        _player.Left += dx;
        _player.Top += dy;
    }

    private void RestoreVideo()
    {
        if (!_settings.VideoEnabled) return;
        ShowPlayer();
    }

    private void ShowPlayer()
    {
        var p = EnsurePlayer();
        if (!p.IsVisible)
        {
            PositionPlayer(p);
            p.Show();
        }
        p.Activate();
    }

    private void VideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player is { IsVisible: true })
        {
            _player.Hide();
            return;
        }
        ShowPlayer();
    }

    private async Task RefreshTrackAsync()
    {
        var track = await _media.GetTrackAsync();
        if (track is null || (string.IsNullOrEmpty(track.Title) && track.ArtBytes is null))
        {
            _lastTitle = "";
            _lastArt = null;
            _lastArtBytes = null;
            TitleText.Text = "Nothing playing";
            TitleText.ToolTip = null;
            TitleText.Opacity = 1.0;
            ArtistText.Text = "Open something in the player";
            ArtistText.ToolTip = null;
            ArtImageHost.Visibility = Visibility.Collapsed;
            ArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        TitleText.Text = string.IsNullOrEmpty(track.Title) ? "Unknown title" : track.Title;
        TitleText.ToolTip = TitleText.Text;
        TitleText.Opacity = 1.0;
        ArtistText.Text = track.Artist;
        ArtistText.ToolTip = null;
        _lastTitle = TitleText.Text;

        if (track.ArtBytes is { Length: > 0 })
        {
            // Same bytes as what's already on screen (the controller hands back
            // one array per track) — reuse the decoded image, don't decode again.
            if (_lastArt is not null && ReferenceEquals(track.ArtBytes, _lastArtBytes))
            {
                ArtBrush.ImageSource = _lastArt;
                ArtImageHost.Opacity = 1.0;
                ArtImageHost.Visibility = Visibility.Visible;
                ArtPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                using var ms = new MemoryStream(track.ArtBytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                _lastArt = bmp;
                _lastArtBytes = track.ArtBytes;
                ArtBrush.ImageSource = bmp;
                ArtImageHost.Opacity = 1.0;
                ArtImageHost.Visibility = Visibility.Visible;
                ArtPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }
            catch { }
        }
        _lastArt = null;
        _lastArtBytes = null;
        ArtImageHost.Visibility = Visibility.Collapsed;
        ArtPlaceholder.Visibility = Visibility.Visible;
        _ = FetchArtFallbackAsync(track.Artist, TitleText.Text);
    }

    // The player gave us no thumbnail; try the iTunes lookup. Applied only if
    // the same track is still showing when the download finishes.
    private async Task FetchArtFallbackAsync(string artist, string title)
    {
        var art = await _artFetcher.TryFetchAsync(artist, title);
        if (art is null || _lastTitle != title) return;
        try
        {
            using var ms = new MemoryStream(art);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            _lastArt = bmp;
            ArtBrush.ImageSource = bmp;
            ArtImageHost.Opacity = 1.0;
            ArtImageHost.Visibility = Visibility.Visible;
            ArtPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void RefreshPlayback()
    {
        var p = _media.GetPlayback();
        _isPlaying = p.IsPlaying;
        PlayPauseGlyph.Text = p.IsPlaying ? "" : "";
        var opacity = p.HasSession ? 1.0 : 0.45;
        PrevButton.Opacity = opacity;
        PlayPauseButton.Opacity = opacity;
        NextButton.Opacity = opacity;
        UpdateProgress();
    }

    private void RefreshTimeline()
    {
        var t = _media.GetTimeline();
        _basePosition = t.Position;
        _duration = t.Duration;
        _baseAsOf = t.AsOf;
        UpdateProgress();
    }

    private string? ActiveProcessName => MediaController.ProcessNameForAppId(_media.ActiveAppId);

    private void RefreshVolume()
    {
        if (VolumeSlider.IsMouseCaptureWithin) return; // user is dragging
        var state = _volume.Get(ActiveProcessName);
        _volUpdatingUI = true;
        VolumeGroup.IsEnabled = state.Available;
        VolumeGroup.Opacity = state.Available ? 1.0 : 0.4;
        if (state.Available) VolumeSlider.Value = state.Volume * 100;
        // A duck is over once the app's volume no longer sits at the quiet
        // level (slider moved, mixer changed) or the controlled app changed.
        if (_ducked && (ActiveProcessName != _duckedProcess ||
                        Math.Abs(state.Volume - (float)_settings.QuietLevel) > 0.05f))
        {
            _ducked = false;
        }
        QuietGlyph.Foreground = (System.Windows.Media.Brush)FindResource(_ducked ? "Accent" : "TextPrimary");
        QuietButton.ToolTip = _ducked
            ? $"Restore volume ({Math.Round(_preDuckVolume * 100)}%)"
            : $"Quiet ({Math.Round(_settings.QuietLevel * 100)}%)";
        _muted = state.Muted;
        MuteGlyph.Text = state.Muted ? "" : VolumeGlyphFor(state.Volume);
        MuteButton.ToolTip = state.Muted ? "Unmute" : "Mute";
        _volUpdatingUI = false;
    }

    private static string VolumeGlyphFor(float volume) => volume switch
    {
        > 0.66f => "",
        > 0.33f => "",
        > 0f => "",
        _ => "",
    };

    // The timeline event only fires occasionally; between events we advance the
    // shown position locally from the last reported (position, timestamp) pair.
    private void UpdateProgress()
    {
        var pos = _basePosition;
        if (_isPlaying) pos += DateTimeOffset.UtcNow - _baseAsOf;
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;

        if (_duration.TotalSeconds > 1)
        {
            var frac = Math.Clamp(pos.TotalSeconds / _duration.TotalSeconds, 0, 1);
            ProgressFill.Width = ProgressHit.ActualWidth * frac;
            if (pos > _duration) pos = _duration;
            ProgressHit.ToolTip = $"{Fmt(pos)} / {Fmt(_duration)}";
        }
        else
        {
            ProgressFill.Width = 0;
            ProgressHit.ToolTip = null;
        }
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => _media.TogglePlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _volume.SetMute(ActiveProcessName, !_muted);
        RefreshVolume();
    }

    private void Quiet_Click(object sender, RoutedEventArgs e)
    {
        var proc = ActiveProcessName;
        if (proc is null) return;
        if (_ducked && proc == _duckedProcess)
        {
            _volume.SetVolume(proc, _preDuckVolume);
            _volume.SetMute(proc, false);
            _ducked = false;
        }
        else
        {
            var state = _volume.Get(proc);
            if (!state.Available) return;
            _preDuckVolume = state.Volume;
            _duckedProcess = proc;
            _volume.SetVolume(proc, (float)_settings.QuietLevel);
            _volume.SetMute(proc, false);
            _ducked = true;
        }
        RefreshVolume();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volUpdatingUI) return;
        _ducked = false; // user took manual control
        _volume.SetVolume(ActiveProcessName, (float)(e.NewValue / 100.0));
        if (_muted && e.NewValue > 0) _volume.SetMute(ActiveProcessName, false);
    }

    private void StreamsButton_Click(object sender, RoutedEventArgs e)
    {
        var streams = _media.GetStreams();
        var menu = new ContextMenu();
        if (streams.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(no matching streams)", IsEnabled = false });
        foreach (var s in streams)
        {
            var title = string.IsNullOrEmpty(s.Title) ? "(no title)" : Trunc(s.Title, 34);
            var prefix = s.IsPlaying ? "▶ " : "";
            var label = $"{prefix}{title} - {AppDisplayName(s.AppId)}";
            var item = new MenuItem
            {
                Header = s.IsSelected ? label + "   (selected)" : label,
                Icon = MenuIcon(IconForApp(s.AppId, s.Title)),
                ToolTip = s.Title,
            };
            var index = s.Index;
            item.Click += (_, _) => _media.SelectStream(index);
            menu.Items.Add(item);
        }
        if (streams.Count > 0)
        {
            menu.Items.Add(new Separator());
            var player = new MenuItem { Header = "Open the player" };
            player.Click += (_, _) => ShowPlayer();
            menu.Items.Add(player);
        }
        menu.PlacementTarget = StreamsButton;
        _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Image? MenuIcon(System.Windows.Media.ImageSource? source) =>
        source is null ? null : new Image { Source = source, Width = 16, Height = 16 };

    /// <summary>
    /// The logo to put next to a stream: the site's favicon when the title
    /// names one (a Twitch tab in Chrome shows Twitch, not Chrome), else the
    /// icon of the app that owns the stream.
    /// </summary>
    private static System.Windows.Media.ImageSource? IconForApp(string appId, string? title)
    {
        var site = IconProvider.SiteFor(title);
        if (site is not null)
        {
            var icon = IconProvider.ForSite(site.Value.Domain);
            if (icon is not null) return icon;
        }
        var process = MediaController.ProcessNameForAppId(appId);
        if (string.IsNullOrEmpty(process)) return null;
        var running = Process.GetProcessesByName(process);
        try
        {
            foreach (var p in running)
            {
                var icon = IconProvider.ForProcess(p.Id);
                if (icon is not null) return icon;
            }
        }
        catch { }
        finally
        {
            foreach (var p in running) p.Dispose();
        }
        return null;
    }

    private static string AppDisplayName(string appId)
    {
        if (appId.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return "Amazon Music";
        if (appId.Contains("_crx_", StringComparison.OrdinalIgnoreCase)) return "Chrome app";
        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (appId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (appId.Contains("308046B", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (appId.Contains("opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
        if (appId.Contains("brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (appId.Contains("vivaldi", StringComparison.OrdinalIgnoreCase)) return "Vivaldi";
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        return appId;
    }

    private void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_duration.TotalSeconds <= 1) return;
        var frac = Math.Clamp(e.GetPosition(ProgressHit).X / ProgressHit.ActualWidth, 0, 1);
        _media.SeekTo(TimeSpan.FromSeconds(frac * _duration.TotalSeconds));
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenAmazonMusic_Click(sender, e);
            return;
        }
        try { DragMove(); } catch { }
    }

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        if (Topmost) ReassertTopmost();
        SaveSettings();
    }

    private void OpenAmazonMusic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://music.amazon.com") { UseShellExecute = true });
        }
        catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // These are independent windows — without this the app would stay
        // alive (capturing, and holding apps at the boost leak level) after
        // the widget is closed.
        try { _mixer?.ReleaseAllBoosts(); } catch { }
        try { _mixer?.Close(); } catch { }
        try { _player?.Close(); } catch { }
        _mixer = null;
        _player = null;
        _volume.Dispose();
        base.OnClosed(e);
    }

    private void SaveSettings()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Topmost = Topmost;
        SettingsStore.Save(_settings);
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 4;
        Top = wa.Bottom - Height - 4;
    }

    private static bool IsOnScreen(double left, double top)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        return left > vl - 50 && top > vt - 50 && left < vl + vw - 50 && top < vt + vh - 50;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
