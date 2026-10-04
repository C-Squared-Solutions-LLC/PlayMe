using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlayMe;

/// <summary>
/// A volume mixer for every app that is making sound, including the ones
/// PlayMe does not control - games, calls, whatever. Sliders run past 100%:
/// see <see cref="AudioBoost"/> for how that is pulled off.
/// </summary>
public partial class MixerWindow : Window
{
    private const string MasterKey = "@master";

    private readonly AudioMixer _mixer = new();
    private readonly Settings _settings;
    private readonly ObservableCollection<MixerRow> _rows = new();
    private readonly Dictionary<string, AudioBoost> _boosts = new();
    private readonly HashSet<string> _starting = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DispatcherTimer _topmostTick = new() { Interval = TimeSpan.FromSeconds(2) };

    public event Action? SettingsChanged;

    /// <summary>
    /// Where the bottom of the panel should sit while it is parked - the top
    /// of whatever is below it. Asked again every refresh, because the panel
    /// grows as apps appear and the video panel moves as it is resized.
    /// Dragging the panel clears it.
    /// </summary>
    public Func<double>? AnchorBottom { get; set; }

    /// <summary>Use a remembered position instead of anchoring.</summary>
    public void PlaceAt(double left, double top)
    {
        AnchorBottom = null;
        Left = left;
        Top = top;
    }

    private void ApplyAnchor()
    {
        if (AnchorBottom is null || !IsVisible || ActualHeight < 1) return;
        double bottom;
        try { bottom = AnchorBottom(); }
        catch { return; }
        var top = Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight);
        if (Math.Abs(Top - top) > 0.5) Top = top;
    }

    public MixerWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        Rows.ItemsSource = _rows;

        _refresh.Tick += (_, _) => RefreshChannels();
        _meter.Tick += (_, _) => UpdateMeters();
        _topmostTick.Tick += (_, _) => ReassertTopmost();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshChannels();
                _refresh.Start();
                _meter.Start();
                _topmostTick.Start();
            }
            else
            {
                // A live boost still needs the slow tick: it is what holds the
                // boosted app at the leak level if anything else moves it.
                if (_boosts.Count == 0) _refresh.Stop();
                _meter.Stop();
                _topmostTick.Stop();
            }
        };

        LocationChanged += (_, _) =>
        {
            if (!IsVisible || AnchorBottom is not null) return;
            _settings.MixerLeft = Left;
            _settings.MixerTop = Top;
            SettingsChanged?.Invoke();
        };

        // The panel grows downward as apps appear; keep its bottom edge pinned
        // so it never creeps over the widget. Moving the window from inside the
        // size pass collapses a SizeToContent window, so do it after layout.
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(ApplyAnchor, DispatcherPriority.Loaded);

        IconProvider.SiteIconArrived += OnIconArrived;
    }

    private void OnIconArrived() => Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible) RefreshChannels();
    });

    /// <summary>Apps parked at the boost leak level that we have to put back.</summary>
    public static void RestoreAfterCrash(Settings settings, AppVolumeController volume)
    {
        if (settings.BoostRestore.Count == 0) return;
        foreach (var (key, original) in settings.BoostRestore)
        {
            var process = System.IO.Path.GetFileNameWithoutExtension(key);
            if (string.IsNullOrEmpty(process)) continue;
            try { volume.SetVolume(process, (float)Math.Clamp(original, 0.05, 1.0)); }
            catch { }
        }
        settings.BoostRestore.Clear();
        SettingsStore.Save(settings);
    }

    private void RefreshChannels()
    {
        ApplyAnchor();
        _mixer.Refresh();

        var wanted = new List<(string Key, string Name, AudioMixer.Channel? Channel)>
        {
            (MasterKey, _mixer.MasterName, null),
        };
        // By name, so rows never jump around under the pointer mid-drag.
        foreach (var channel in _mixer.Channels.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            wanted.Add((channel.Key, channel.Name, channel));

        // Drop rows whose app has gone away, releasing any boost with them.
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (wanted.Any(w => w.Key == _rows[i].Key)) continue;
            ReleaseBoost(_rows[i].Key, null);
            _rows.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var (key, name, channel) = wanted[i];
            var row = _rows.FirstOrDefault(r => r.Key == key);
            if (row is null)
            {
                row = new MixerRow(key, key == MasterKey ? 100 : 300);
                row.Applied += ApplyRow;
                _rows.Insert(Math.Min(i, _rows.Count), row);
            }
            else
            {
                var at = _rows.IndexOf(row);
                if (at != i && i < _rows.Count) _rows.Move(at, i);
            }
            row.Name = name;
            row.Icon = channel is null
                ? null
                : channel.ExePath is not null ? IconProvider.ForExe(channel.ExePath) : null;
            row.Tip = channel?.ExePath ?? name;

            // Don't yank a slider out from under the user, and don't overwrite a
            // level whose boost is still starting up.
            var busy = _starting.Contains(key) ||
                       DateTime.UtcNow - row.LastTouchedUtc < TimeSpan.FromSeconds(2);

            row.Suppress = true;
            if (channel is null)
            {
                if (!busy)
                {
                    row.Percent = Math.Round(_mixer.MasterVolume * 100);
                    row.Muted = _mixer.MasterMuted;
                }
                row.SliderTip = "Windows output volume";
            }
            else if (_boosts.TryGetValue(key, out var boost) && boost.Active)
            {
                if (!busy)
                {
                    row.Percent = Math.Round(boost.Gain * 100);
                    row.Muted = _mixer.MutedOf(channel);
                }
                row.SliderTip = "Boosted past 100% by PlayMe - adds about 40 ms of delay";
                // Something else may have nudged the app's own level; put it back.
                if (Math.Abs(_mixer.VolumeOf(channel) - AudioBoost.LeakVolume) > 0.01f)
                    _mixer.SetVolume(channel, AudioBoost.LeakVolume);
            }
            else
            {
                if (!busy)
                {
                    row.Percent = Math.Round(_mixer.VolumeOf(channel) * 100);
                    row.Muted = _mixer.MutedOf(channel);
                }
                if (!row.SliderTip.StartsWith("Windows would not", StringComparison.Ordinal))
                {
                    row.SliderTip = AudioBoost.IsSupported
                        ? "Drag past 100% to boost this app above what Windows allows"
                        : "Boost needs Windows 11 (or Windows 10 build 20348+)";
                }
            }
            row.Suppress = false;
        }
    }

    private void UpdateMeters()
    {
        foreach (var row in _rows)
        {
            if (row.Key == MasterKey)
            {
                row.Peak = _mixer.MasterPeak;
                continue;
            }
            var channel = _mixer.Channels.FirstOrDefault(c => c.Key == row.Key);
            if (channel is null) continue;
            var peak = _mixer.PeakOf(channel);
            if (_boosts.TryGetValue(row.Key, out var boost) && boost.Active)
                peak = Math.Max(peak, boost.Peak);
            row.Peak = peak;
        }
    }

    private void ApplyRow(MixerRow row)
    {
        if (row.Key == MasterKey)
        {
            _mixer.MasterVolume = (float)(row.Percent / 100.0);
            return;
        }

        var channel = _mixer.Channels.FirstOrDefault(c => c.Key == row.Key);
        if (channel is null) return;

        if (row.Percent > 100.5)
        {
            if (!AudioBoost.IsSupported)
            {
                SnapToFull(row, channel, "Boost needs Windows 11 (or Windows 10 build 20348+)");
                return;
            }

            var gain = (float)(row.Percent / 100.0);
            if (_boosts.TryGetValue(row.Key, out var live) && live.Active)
            {
                live.Gain = gain;
                _mixer.SetVolume(channel, AudioBoost.LeakVolume);
                _settings.BoostLevels[row.Key] = row.Percent;
                SettingsChanged?.Invoke();
                return;
            }

            // Starting one means activating WASAPI capture, which can take a
            // moment - do it off the UI thread so the slider stays live.
            if (!_starting.Add(row.Key)) return;
            var key = row.Key;
            var pid = channel.ProcessId;
            var original = _mixer.VolumeOf(channel);
            var boost = new AudioBoost();
            Task.Run(() => boost.Start(pid, gain)).ContinueWith(task =>
            {
                _starting.Remove(key);
                var started = task.Status == TaskStatus.RanToCompletion && task.Result;
                var current = _mixer.Channels.FirstOrDefault(c => c.Key == key);
                if (!started || current is null)
                {
                    var why = boost.LastError is { Length: > 0 } error
                        ? "Windows would not let PlayMe capture this app: " + error
                        : "Windows would not let PlayMe capture this app";
                    boost.Dispose();
                    SnapToFull(row, current, why);
                    return;
                }
                _boosts[key] = boost;
                _settings.BoostRestore[key] = original;
                _settings.BoostLevels[key] = row.Percent;
                _mixer.SetVolume(current, AudioBoost.LeakVolume);
                SettingsChanged?.Invoke();
                _refresh.Start(); // keeps the leak level pinned even if hidden
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            ReleaseBoost(row.Key, channel);
            _mixer.SetVolume(channel, (float)(row.Percent / 100.0));
        }
    }

    /// <summary>Put a row back to 100% and say why it wouldn't go higher.</summary>
    private void SnapToFull(MixerRow row, AudioMixer.Channel? channel, string why)
    {
        row.Suppress = true;
        row.Percent = 100;
        row.Suppress = false;
        if (channel is not null) _mixer.SetVolume(channel, 1f);
        row.SliderTip = why;
    }

    private void ReleaseBoost(string key, AudioMixer.Channel? channel)
    {
        if (_boosts.Remove(key, out var boost))
        {
            boost.Stop();
            boost.Dispose();
            // Put the app's own output back where the user left it.
            if (channel is not null)
            {
                var original = _settings.BoostRestore.TryGetValue(key, out var v) ? (float)v : 1f;
                _mixer.SetVolume(channel, Math.Clamp(original, 0.05f, 1f));
            }
        }
        var changed = _settings.BoostRestore.Remove(key);
        changed |= _settings.BoostLevels.Remove(key);
        if (changed) SettingsChanged?.Invoke();
    }

    public void ReleaseAllBoosts()
    {
        foreach (var key in _boosts.Keys.ToList())
        {
            var channel = _mixer.Channels.FirstOrDefault(c => c.Key == key);
            ReleaseBoost(key, channel);
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string key) return;
        var row = _rows.FirstOrDefault(r => r.Key == key);
        if (row is null) return;
        if (key == MasterKey)
        {
            _mixer.MasterMuted = !_mixer.MasterMuted;
            row.Muted = _mixer.MasterMuted;
            return;
        }
        var channel = _mixer.Channels.FirstOrDefault(c => c.Key == key);
        if (channel is null) return;
        var mute = !_mixer.MutedOf(channel);
        _mixer.SetMute(channel, mute);
        row.Muted = mute;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            if (row.Key == MasterKey) continue;
            var channel = _mixer.Channels.FirstOrDefault(c => c.Key == row.Key);
            ReleaseBoost(row.Key, channel);
            if (channel is not null)
            {
                _mixer.SetVolume(channel, 1f);
                _mixer.SetMute(channel, false);
            }
            row.Suppress = true;
            row.Percent = 100;
            row.Muted = false;
            row.Suppress = false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Moving it by hand means the user has picked a spot; stop parking it.
        AnchorBottom = null;
        try { DragMove(); } catch { }
        _settings.MixerLeft = Left;
        _settings.MixerTop = Top;
        SettingsChanged?.Invoke();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private void ReassertTopmost()
    {
        if (!IsVisible || !Topmost) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnClosed(EventArgs e)
    {
        IconProvider.SiteIconArrived -= OnIconArrived;
        _refresh.Stop();
        _meter.Stop();
        _topmostTick.Stop();
        ReleaseAllBoosts();
        _mixer.Dispose();
        base.OnClosed(e);
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
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

public sealed class MixerRow : INotifyPropertyChanged
{
    private static readonly Brush TextPrimary = Frozen("#F2F3F7");
    private static readonly Brush TextSecondary = Frozen("#9DA2B4");
    private static readonly Brush Accent = Frozen("#3BC8D6");
    private static readonly Brush BoostBrush = Frozen("#F5A524");
    private static readonly Brush Dim = Frozen("#55FFFFFF");

    private string _name = "";
    private ImageSource? _icon;
    private double _percent;
    private double _peak;
    private bool _muted;
    private string _tip = "";
    private string _sliderTip = "";

    public MixerRow(string key, double maximum)
    {
        Key = key;
        Maximum = maximum;
    }

    /// <summary>Raised when the slider was moved by the user, not by a refresh.</summary>
    public event Action<MixerRow>? Applied;

    public string Key { get; }
    public double Maximum { get; }
    internal bool Suppress;

    public Visibility UnityTickVisibility => Maximum > 100 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Notify(); }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set { if (ReferenceEquals(_icon, value)) return; _icon = value; Notify(); Notify(nameof(GlyphVisibility)); }
    }

    public string Tip
    {
        get => _tip;
        set { if (_tip == value) return; _tip = value; Notify(); }
    }

    public string SliderTip
    {
        get => _sliderTip;
        set { if (_sliderTip == value) return; _sliderTip = value; Notify(); }
    }

    /// <summary>When the user last moved this slider themselves.</summary>
    public DateTime LastTouchedUtc { get; private set; } = DateTime.MinValue;

    public double Percent
    {
        get => _percent;
        set
        {
            if (Math.Abs(_percent - value) < 0.5) return;
            _percent = value;
            Notify();
            Notify(nameof(PercentText));
            Notify(nameof(PercentBrush));
            Notify(nameof(BarBrush));
            if (Suppress) return;
            LastTouchedUtc = DateTime.UtcNow;
            Applied?.Invoke(this);
        }
    }

    public double Peak
    {
        get => _peak;
        set { if (Math.Abs(_peak - value) < 0.004) return; _peak = value; Notify(); }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            Notify();
            Notify(nameof(MuteGlyph));
            Notify(nameof(MuteBrush));
            Notify(nameof(MuteTip));
            Notify(nameof(NameBrush));
            Notify(nameof(BarBrush));
        }
    }

    public string PercentText => $"{Math.Round(_percent)}%";
    public Brush PercentBrush => _percent > 100.5 ? BoostBrush : TextSecondary;
    public Brush NameBrush => _muted ? TextSecondary : TextPrimary;
    public Brush BarBrush => _muted ? Dim : _percent > 100.5 ? BoostBrush : Accent;
    public string MuteGlyph => _muted ? "" : "";
    public Brush MuteBrush => _muted ? BoostBrush : TextSecondary;
    public string MuteTip => _muted ? "Unmute" : "Mute";

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
