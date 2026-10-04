using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PlayMe;

/// <summary>
/// PlayMe's own browser panel: it opens sites itself rather than mirroring
/// someone else's window, which is what lets it follow you across virtual
/// desktops - nothing can cloak it or stop it drawing. Several sites stay
/// open at once; only one is shown, the rest keep playing.
/// </summary>
public partial class PlayerWindow : Window
{
    private sealed class Tab
    {
        public required Site Site { get; set; }
        public required WebView2 View { get; init; }
        public string Title { get; set; } = "";
        public double Volume { get; set; } = 1.0;
        public bool Muted { get; set; }
        public bool VideoOnly { get; set; }
        public double Aspect { get; set; }   // stream width / height, 0 if unknown
    }

    private readonly Settings _settings;
    private readonly List<Tab> _tabs = new();
    private readonly DispatcherTimer _topmostTick = new() { Interval = TimeSpan.FromSeconds(2) };
    private Tab? _active;
    private CoreWebView2Environment? _environment;
    private bool _menuOpen;
    private bool _interactive;
    private bool _volumeUpdating;

    /// <summary>Width:height the window is being held to, or null.</summary>
    private double? AspectLock { get; set; }

    // What the panel costs around the web view, in DIPs: the bar, the resize
    // strip and the border.
    private const double ChromeHeight = 28 + 8 + 2;
    private const double ChromeWidth = 2;

    public event Action? SettingsChanged;

    public PlayerWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;

        Width = Math.Clamp(settings.VideoWidth, 240, 1920);
        Height = Math.Clamp(settings.VideoHeight, 140, 1200);

        Menu.Opened += (_, _) => { _menuOpen = true; BuildMenu(); };
        Menu.Closed += (_, _) => _menuOpen = false;

        _topmostTick.Tick += (_, _) => ReassertTopmost();
        _topmostTick.Start();

        SizeChanged += (_, _) =>
        {
            _settings.VideoWidth = Width;
            _settings.VideoHeight = Height;
            SettingsChanged?.Invoke();
        };
        LocationChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _settings.VideoLeft = Left;
            _settings.VideoTop = Top;
            SettingsChanged?.Invoke();
        };

        IconProvider.SiteIconArrived += OnIconArrived;
        Loaded += async (_, _) => await StartAsync();
    }

    public bool HasTabs => _tabs.Count > 0;

    private void OnIconArrived() => Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible) BuildQuickStrip();
    });

    /// <summary>Favourites, defaulting to a few obvious starting points.</summary>
    private List<Site> Favourites()
    {
        var saved = _settings.PlayerSites
            .Select(SiteCatalog.Parse)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
        return saved.Count > 0 ? saved : SiteCatalog.Defaults.ToList();
    }

    private async Task StartAsync()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayMe", "browser");
            Directory.CreateDirectory(folder);

            // Sites refuse to autoplay without a click otherwise, which is the
            // whole point of a panel you glance at.
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            _environment = await CoreWebView2Environment.CreateAsync(null, folder, options);
        }
        catch (Exception ex)
        {
            ShowStatus("The WebView2 runtime isn't available.",
                "It ships with Windows 11. " + ex.Message);
            return;
        }

        BuildQuickStrip();
        var last = SiteCatalog.Parse(_settings.PlayerUrl) ?? Favourites().FirstOrDefault();
        if (last is not null) await OpenAsync(last);
        else ShowStatus("Nothing open.", "Use the globe button to open a site.");
    }

    /// <summary>Show a site, reusing its tab if it is already open.</summary>
    public async Task OpenAsync(Site site)
    {
        var existing = _tabs.FirstOrDefault(t =>
            t.Site.Url.Equals(site.Url, StringComparison.OrdinalIgnoreCase) ||
            t.Site.Domain.Equals(site.Domain, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Activate(existing);
            if (!existing.Site.Url.Equals(site.Url, StringComparison.OrdinalIgnoreCase))
            {
                existing.Site = site;
                try { existing.View.CoreWebView2?.Navigate(site.Url); } catch { }
            }
            return;
        }

        if (_environment is null) return;
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x0B, 0x0C, 0x10) };
        var tab = new Tab
        {
            Site = site,
            View = view,
            Title = site.Title,
            Volume = Math.Clamp(_settings.PlayerVolume, 0, 1),
            Muted = _settings.PlayerMuted,
        };
        TabHost.Children.Add(view);
        _tabs.Add(tab);
        Activate(tab);

        try
        {
            await view.EnsureCoreWebView2Async(_environment);
            var core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = core.DocumentTitle;
                if (ReferenceEquals(tab, _active)) UpdateTitle();
                BuildQuickStrip();
            };
            core.NewWindowRequested += (_, e) =>
            {
                // Keep pop-outs inside the panel instead of throwing them at
                // the default browser.
                e.Handled = true;
                try { core.Navigate(e.Uri); } catch { }
            };
            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    var json = e.TryGetWebMessageAsString();
                    if (json is null || !json.Contains("playme-video")) return;
                    var node = System.Text.Json.Nodes.JsonNode.Parse(json);
                    var w = node?["w"]?.GetValue<double>() ?? 0;
                    var h = node?["h"]?.GetValue<double>() ?? 0;
                    if (w <= 0 || h <= 0) return;
                    tab.Aspect = w / h;
                    Dispatcher.InvokeAsync(() => SnapToAspect(tab));
                }
                catch { }
            };
            core.ProcessFailed += (_, _) => Dispatcher.InvokeAsync(() =>
                ShowStatus("That site's process stopped.", "Options > Reload to try again."));
            await core.AddScriptToExecuteOnDocumentCreatedAsync(VolumeScript);
            core.IsMuted = tab.Muted;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.addEventListener('load',function(){{window.__playme&&(window.__playme.fill={(_settings.PlayerFill ? "true" : "false")});}});");
            tab.VideoOnly = _settings.PlayerVideoOnly
                .Any(d => d.Equals(site.Domain, StringComparison.OrdinalIgnoreCase));
            core.NavigationCompleted += (_, _) =>
            {
                // Sites build their player after the page loads, so give it a
                // moment before cropping to it.
                if (!tab.VideoOnly) return;
                Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(1800);
                    ApplyVideoOnly(tab);
                });
            };
            core.Navigate(site.Url);
            SetTabVolume(tab, tab.Volume);
            HideStatus();
        }
        catch (Exception ex)
        {
            ShowStatus("Couldn't open " + site.Domain, ex.Message);
        }

        _settings.PlayerUrl = site.Url;
        SettingsChanged?.Invoke();
        BuildQuickStrip();
    }

    private void Activate(Tab tab)
    {
        _active = tab;
        foreach (var t in _tabs)
        {
            t.View.Visibility = ReferenceEquals(t, tab) ? Visibility.Visible : Visibility.Collapsed;
        }
        _settings.PlayerUrl = tab.Site.Url;
        SettingsChanged?.Invoke();
        ShowVolumeOf(tab);
        ShowVideoOnlyOf(tab);
        SnapToAspect(tab);
        UpdateTitle();
        HideStatus();
        BuildQuickStrip();
    }

    private void CloseTab(Tab tab)
    {
        _tabs.Remove(tab);
        TabHost.Children.Remove(tab.View);
        try { tab.View.Dispose(); } catch { }
        if (ReferenceEquals(_active, tab)) _active = null;
        var next = _tabs.LastOrDefault();
        if (next is not null) Activate(next);
        else
        {
            UpdateTitle();
            ShowStatus("Nothing open.", "Use the globe button to open a site.");
        }
        BuildQuickStrip();
    }

    private void ShowVolumeOf(Tab tab)
    {
        _volumeUpdating = true;
        VolumeSlider.Value = Math.Round(tab.Volume * 100);
        _volumeUpdating = false;
        MuteGlyph.Text = tab.Muted ? "" : tab.Volume > 0.66 ? "" : tab.Volume > 0.33 ? "" : "";
        MuteGlyph.Foreground = (Brush)FindResource(tab.Muted ? "Accent" : "TextPrimary");
        MuteButton.ToolTip = tab.Muted ? "Unmute" : "Mute what the player is playing";
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeUpdating || _active is null) return;
        var tab = _active;
        SetTabVolume(tab, e.NewValue / 100.0);
        if (tab.Muted && e.NewValue > 0) SetTabMuted(tab, false);
        _settings.PlayerVolume = tab.Volume;
        SettingsChanged?.Invoke();
        ShowVolumeOf(tab);
    }

    private void VideoOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        SetVideoOnly(_active, !_active.VideoOnly);
    }

    private void ShowVideoOnlyOf(Tab tab)
    {
        VideoOnlyGlyph.Foreground = (Brush)FindResource(tab.VideoOnly ? "Accent" : "TextPrimary");
        VideoOnlyButton.ToolTip = tab.VideoOnly
            ? "Showing just the picture - click to show the whole page"
            : "Video only - show just the picture, without the chat and menus";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        SetTabMuted(_active, !_active.Muted);
        _settings.PlayerMuted = _active.Muted;
        SettingsChanged?.Invoke();
        ShowVolumeOf(_active);
    }

    private void UpdateTitle()
    {
        var title = _active is null
            ? "PlayMe"
            : string.IsNullOrWhiteSpace(_active.Title) ? _active.Site.Title : _active.Title;
        TitleText.Text = Trunc(title, 64);
        TitleText.ToolTip = _active?.Site.Url;
    }

    private void ShowStatus(string text, string hint = "")
    {
        StatusText.Text = text;
        StatusHint.Text = hint;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Favourites first, then anything else that happens to be open, each as
    /// its site's icon. The one you're looking at is underlined.
    /// </summary>
    private void BuildQuickStrip()
    {
        var entries = new List<(Site Site, Tab? Tab)>();
        foreach (var favourite in Favourites())
        {
            var tab = _tabs.FirstOrDefault(t =>
                t.Site.Domain.Equals(favourite.Domain, StringComparison.OrdinalIgnoreCase));
            entries.Add((favourite, tab));
        }
        foreach (var tab in _tabs)
        {
            if (entries.Any(e => e.Tab is not null && ReferenceEquals(e.Tab, tab))) continue;
            entries.Add((tab.Site, tab));
        }

        QuickStrip.Children.Clear();
        foreach (var (site, tab) in entries)
        {
            var isActive = tab is not null && ReferenceEquals(tab, _active);
            var button = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Style = (Style)FindResource("BarButton"),
                ToolTip = tab is null
                    ? $"Open {site.Title}"
                    : $"{(isActive ? "Showing" : "Open")}: {Trunc(tab.Title.Length > 0 ? tab.Title : site.Title, 60)}",
                Opacity = isActive ? 1.0 : tab is not null ? 0.85 : 0.5,
            };

            var content = new Grid();
            var icon = IconProvider.ForSite(site.Domain);
            if (icon is not null)
            {
                content.Children.Add(new Image
                {
                    Source = icon,
                    Width = 16,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = site.Title.Length > 0 ? site.Title[..1].ToUpperInvariant() : "?",
                    Foreground = (Brush)FindResource("TextPrimary"),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (tab is not null)
            {
                content.Children.Add(new Border
                {
                    Height = 2,
                    Background = (Brush)FindResource(isActive ? "Accent" : "TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(3, 0, 3, 0),
                });
            }
            button.Content = content;

            var target = site;
            var openTab = tab;
            button.Click += async (_, _) =>
            {
                if (openTab is not null) Activate(openTab);
                else await OpenAsync(target);
            };
            button.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowSiteMenu(target, openTab, button);
            };
            QuickStrip.Children.Add(button);
        }
    }

    private void ShowSiteMenu(Site site, Tab? tab, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        var isFavourite = Favourites().Any(f =>
            f.Domain.Equals(site.Domain, StringComparison.OrdinalIgnoreCase));

        if (tab is not null)
        {
            var close = new MenuItem { Header = "Close this site" };
            close.Click += (_, _) => CloseTab(tab);
            menu.Items.Add(close);

            var mute = new MenuItem { Header = "Mute this site", IsCheckable = true, IsChecked = tab.Muted };
            mute.Click += (_, _) => SetTabMuted(tab, mute.IsChecked);
            menu.Items.Add(mute);

            var videoOnly = new MenuItem { Header = "Video only", IsCheckable = true, IsChecked = tab.VideoOnly };
            videoOnly.Click += (_, _) => SetVideoOnly(tab, videoOnly.IsChecked);
            menu.Items.Add(videoOnly);
            menu.Items.Add(new Separator());
        }

        var favourite = new MenuItem
        {
            Header = isFavourite ? "Remove from favourites" : "Add to favourites",
        };
        favourite.Click += (_, _) =>
        {
            if (isFavourite) RemoveFavourite(site);
            else AddFavourite(site);
        };
        menu.Items.Add(favourite);

        _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
    }

    private void AddFavourite(Site site)
    {
        var sites = _settings.PlayerSites.Count > 0
            ? _settings.PlayerSites
            : SiteCatalog.Defaults.Select(d => d.Url).ToList();
        if (!sites.Any(s => string.Equals(s, site.Url, StringComparison.OrdinalIgnoreCase)))
            sites.Add(site.Url);
        _settings.PlayerSites = sites;
        SettingsChanged?.Invoke();
        BuildQuickStrip();
    }

    private void RemoveFavourite(Site site)
    {
        var sites = _settings.PlayerSites.Count > 0
            ? _settings.PlayerSites
            : SiteCatalog.Defaults.Select(d => d.Url).ToList();
        sites.RemoveAll(s =>
        {
            var parsed = SiteCatalog.Parse(s);
            return parsed is not null &&
                   parsed.Domain.Equals(site.Domain, StringComparison.OrdinalIgnoreCase);
        });
        _settings.PlayerSites = sites;
        SettingsChanged?.Invoke();
        BuildQuickStrip();
    }

    private void SetTabMuted(Tab tab, bool muted)
    {
        tab.Muted = muted;
        try
        {
            if (tab.View.CoreWebView2 is { } core) core.IsMuted = muted;
        }
        catch { }
        if (ReferenceEquals(tab, _active)) ShowVolumeOf(tab);
    }

    private async void SetTabVolume(Tab tab, double volume)
    {
        tab.Volume = Math.Clamp(volume, 0, 1);
        try
        {
            if (tab.View.CoreWebView2 is { } core)
            {
                await core.ExecuteScriptAsync(
                    $"window.__playme && (window.__playme.volume={tab.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}, window.__playmeApply())");
            }
        }
        catch { }
    }

    // Applied to every page as it loads: sites create their media elements
    // whenever they feel like it, so watch for new ones rather than setting
    // the volume once and hoping.
    private const string VolumeScript = @"
(function () {
  if (window.__playme) return;
  window.__playme = { volume: 1, fill: false };
  var apply = function () {
    var media = document.querySelectorAll('video,audio');
    for (var i = 0; i < media.length; i++) {
      try { media[i].volume = window.__playme.volume; } catch (e) { }
    }
  };
  window.__playmeApply = apply;
  try {
    new MutationObserver(apply).observe(document.documentElement, { childList: true, subtree: true });
  } catch (e) { }
  document.addEventListener('play', apply, true);
  setInterval(apply, 1500);
})();";

    // Zooms the page so its biggest <video> fills the panel, and keeps doing
    // so as the site re-lays-out. Scaling the document is the one trick that
    // works everywhere; picking apart each site's markup would not.
    private const string VideoOnlyScript = @"
(function () {
  if (window.__playmeCrop) return;
  var saved = null;
  var savedRoot = null;
  var timer = null;
  var lastW = 0, lastH = 0;

  var biggest = function () {
    var best = null, area = 0;
    var all = document.querySelectorAll('video');
    for (var i = 0; i < all.length; i++) {
      var v = all[i];
      if (v.readyState === 0 && !v.srcObject) continue;
      var w = v.offsetWidth, h = v.offsetHeight;
      var a = w * h;
      if (a > area && w > 40 && h > 40) { best = v; area = a; }
    }
    return best;
  };

  var set = function (el, prop, value) {
    try { el.style.setProperty(prop, value, 'important'); } catch (e) { }
  };

  var report = function (v) {
    var vw = v.videoWidth || 0, vh = v.videoHeight || 0;
    if (vw <= 0 || vh <= 0 || (vw === lastW && vh === lastH)) return;
    lastW = vw; lastH = vh;
    try {
      window.chrome.webview.postMessage(JSON.stringify({ t: 'playme-video', w: vw, h: vh }));
    } catch (e) { }
  };

  // Rather than measuring the page and zooming it - which goes wrong the
  // moment a site swaps its player, as Twitch does for ads - lift the video
  // itself to cover the window and let object-fit do the arithmetic.
  var apply = function () {
    var v = biggest();
    if (!v || !v.isConnected) return;
    if (!saved || saved.el !== v) {
      restoreVideo();
      saved = { el: v, style: v.getAttribute('style'), parent: v.parentNode, next: v.nextSibling };
    }
    var fill = window.__playme && window.__playme.fill;
    set(v, 'position', 'fixed');
    set(v, 'left', '0'); set(v, 'top', '0'); set(v, 'right', 'auto'); set(v, 'bottom', 'auto');
    set(v, 'width', '100vw'); set(v, 'height', '100vh');
    set(v, 'min-width', '0'); set(v, 'min-height', '0');
    set(v, 'max-width', 'none'); set(v, 'max-height', 'none');
    set(v, 'margin', '0'); set(v, 'padding', '0');
    set(v, 'transform', 'none');
    set(v, 'object-fit', fill ? 'cover' : 'contain');
    set(v, 'background', '#000');
    set(v, 'z-index', '2147483647');
    set(v, 'display', 'block');
    set(v, 'visibility', 'visible');
    set(v, 'opacity', '1');

    var root = document.documentElement, body = document.body;
    if (savedRoot === null) {
      savedRoot = { root: root.getAttribute('style'), body: body ? body.getAttribute('style') : null };
    }
    set(root, 'overflow', 'hidden');
    set(root, 'background', '#000');
    if (body) { set(body, 'overflow', 'hidden'); set(body, 'background', '#000'); }

    // position:fixed is relative to the nearest transformed ancestor, not the
    // window, and players are full of those - if it did not land on the
    // window, take the element out to the top level.
    var r = v.getBoundingClientRect();
    if (r.left > 1 || r.top > 1 ||
        r.width < window.innerWidth - 2 || r.height < window.innerHeight - 2) {
      try { if (v.parentNode !== root) root.appendChild(v); } catch (e) { }
    }
    report(v);
  };

  var restoreVideo = function () {
    if (!saved) return;
    var v = saved.el;
    try {
      if (saved.style === null) v.removeAttribute('style');
      else v.setAttribute('style', saved.style);
    } catch (e) { }
    try {
      if (saved.parent && v.parentNode !== saved.parent) {
        var next = saved.next && saved.next.parentNode === saved.parent ? saved.next : null;
        saved.parent.insertBefore(v, next);
      }
    } catch (e) { }
    saved = null;
  };

  window.__playmeCrop = function (on) {
    if (on) {
      if (!biggest()) return 'novideo';
      apply();
      if (!timer) timer = setInterval(apply, 700);
      window.addEventListener('resize', apply);
      return 'on';
    }
    if (timer) { clearInterval(timer); timer = null; }
    window.removeEventListener('resize', apply);
    restoreVideo();
    if (savedRoot !== null) {
      var root = document.documentElement, body = document.body;
      try {
        if (savedRoot.root === null) root.removeAttribute('style');
        else root.setAttribute('style', savedRoot.root);
        if (body) {
          if (savedRoot.body === null) body.removeAttribute('style');
          else body.setAttribute('style', savedRoot.body);
        }
      } catch (e) { }
      savedRoot = null;
    }
    return 'off';
  };
})();";

    private async void ApplyVideoOnly(Tab tab)
    {
        try
        {
            if (tab.View.CoreWebView2 is not { } core) return;
            await core.ExecuteScriptAsync(VideoOnlyScript);
            var result = await core.ExecuteScriptAsync(
                $"window.__playmeCrop && window.__playmeCrop({(tab.VideoOnly ? "true" : "false")})");

            if (tab.VideoOnly && result is not null && result.Contains("novideo"))
            {
                ShowStatus("No video on this page yet.",
                    "Start something playing, then turn video-only on again.");
                await Task.Delay(2500);
                if (ReferenceEquals(tab, _active)) HideStatus();
            }
        }
        catch { }
    }

    /// <summary>
    /// Match the panel to the shape of what is playing, so a 4:3 or ultrawide
    /// stream doesn't sit in bars. Only while video-only is on - otherwise the
    /// panel is showing a web page and should stay whatever shape you made it.
    /// </summary>
    private void SnapToAspect(Tab tab)
    {
        if (!ReferenceEquals(tab, _active)) return;
        AspectLock = tab.VideoOnly && tab.Aspect > 0.05 ? tab.Aspect : null;
        if (AspectLock is not double aspect || _settings.PlayerFill) return;
        var content = Math.Max(40, Width - ChromeWidth);
        var height = Math.Round(content / aspect) + ChromeHeight;
        if (Math.Abs(height - Height) > 1) Height = Math.Clamp(height, MinHeight, 1200);
    }

    private void SetFill(bool on)
    {
        _settings.PlayerFill = on;
        SettingsChanged?.Invoke();
        foreach (var tab in _tabs) PushFill(tab);
        if (_active is not null) SnapToAspect(_active);
    }

    private async void PushFill(Tab tab)
    {
        try
        {
            if (tab.View.CoreWebView2 is not { } core) return;
            await core.ExecuteScriptAsync(
                $"window.__playme && (window.__playme.fill={(_settings.PlayerFill ? "true" : "false")}, window.__playmeApply && window.__playmeApply())");
            if (tab.VideoOnly) ApplyVideoOnly(tab);
        }
        catch { }
    }

    private void SetVideoOnly(Tab tab, bool on)
    {
        tab.VideoOnly = on;
        var domains = _settings.PlayerVideoOnly;
        domains.RemoveAll(d => d.Equals(tab.Site.Domain, StringComparison.OrdinalIgnoreCase));
        if (on) domains.Add(tab.Site.Domain);
        _settings.PlayerVideoOnly = domains;
        SettingsChanged?.Invoke();
        ApplyVideoOnly(tab);
        SnapToAspect(tab);
        if (ReferenceEquals(tab, _active)) ShowVideoOnlyOf(tab);
    }

    private void Go_Click(object sender, RoutedEventArgs e) => AskForSite();

    private void AskForSite()
    {
        var answer = Prompt.Ask(
            "PlayMe",
            "Address, domain, or a Twitch channel name:",
            _active?.Site.Url ?? "https://music.amazon.com",
            Left + 20,
            Top + 40);
        var site = SiteCatalog.Parse(answer);
        if (site is null) return;
        _ = OpenAsync(site);
    }

    // The panel never takes focus, which is what keeps it out of your way -
    // but you cannot type into it either. This lends it focus on request.
    private void Interact_Click(object sender, RoutedEventArgs e) => SetInteractive(!_interactive);

    private void SetInteractive(bool on)
    {
        _interactive = on;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var ex = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE);
        InteractGlyph.Foreground = (Brush)FindResource(on ? "Accent" : "TextPrimary");
        InteractButton.ToolTip = on
            ? "Typing enabled - click again to go back to not stealing focus"
            : "Let the panel take keyboard focus - click this before typing or signing in";
        if (!on) return;
        System.Windows.Window.GetWindow(this)?.Activate();
        _active?.View.Focus();
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        BuildMenu();
        Menu.PlacementTarget = OptionsButton;
        Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _menuOpen = true;
        Menu.IsOpen = true;
    }

    private void BuildMenu()
    {
        Menu.Items.Clear();

        var open = new MenuItem { Header = "Open a site..." };
        open.Click += (_, _) => AskForSite();
        Menu.Items.Add(open);

        if (_active is not null)
        {
            var site = _active.Site;
            var isFavourite = Favourites().Any(f =>
                f.Domain.Equals(site.Domain, StringComparison.OrdinalIgnoreCase));
            var favourite = new MenuItem
            {
                Header = isFavourite ? $"Remove {site.Domain} from favourites" : $"Add {site.Domain} to favourites",
            };
            favourite.Click += (_, _) =>
            {
                if (isFavourite) RemoveFavourite(site);
                else AddFavourite(site);
            };
            Menu.Items.Add(favourite);

            var reload = new MenuItem { Header = "Reload" };
            reload.Click += (_, _) => { try { _active?.View.CoreWebView2?.Reload(); } catch { } };
            Menu.Items.Add(reload);

            var external = new MenuItem { Header = "Open in your browser" };
            external.Click += (_, _) =>
            {
                try
                {
                    var url = _active?.View.CoreWebView2?.Source ?? site.Url;
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { }
            };
            Menu.Items.Add(external);

            Menu.Items.Add(new Separator());

            var volume = new MenuItem { Header = "Volume" };
            foreach (var level in new[] { 100, 75, 50, 25, 10 })
            {
                var item = new MenuItem
                {
                    Header = $"{level}%",
                    IsCheckable = true,
                    IsChecked = Math.Abs(_active.Volume * 100 - level) < 1,
                };
                var value = level / 100.0;
                var tab = _active;
                item.Click += (_, _) => SetTabVolume(tab, value);
                volume.Items.Add(item);
            }
            Menu.Items.Add(volume);

            var videoOnly = new MenuItem
            {
                Header = "Video only",
                IsCheckable = true,
                IsChecked = _active.VideoOnly,
                ToolTip = "Zoom the page down to just its video, dropping the chat, menus and the rest.",
            };
            var cropTab = _active;
            videoOnly.Click += (_, _) => SetVideoOnly(cropTab, videoOnly.IsChecked);
            Menu.Items.Add(videoOnly);

            var fill = new MenuItem
            {
                Header = "Fill the panel",
                IsCheckable = true,
                IsChecked = _settings.PlayerFill,
                ToolTip = "Crop the edges of the picture to fill the panel, instead of matching "
                    + "the panel to the shape of what is playing.",
            };
            fill.Click += (_, _) => SetFill(fill.IsChecked);
            Menu.Items.Add(fill);

            var mute = new MenuItem { Header = "Mute this site", IsCheckable = true, IsChecked = _active.Muted };
            var muteTab = _active;
            mute.Click += (_, _) => SetTabMuted(muteTab, mute.IsChecked);
            Menu.Items.Add(mute);

            var close = new MenuItem { Header = $"Close {site.Domain}" };
            var closeTab = _active;
            close.Click += (_, _) => CloseTab(closeTab);
            Menu.Items.Add(close);
        }

        Menu.Items.Add(new Separator());
        var follow = new MenuItem
        {
            Header = "Move with the widget",
            IsCheckable = true,
            IsChecked = _settings.VideoFollowsWidget,
            ToolTip = "Keep the panel pinned to the widget when you drag the widget around.",
        };
        follow.Click += (_, _) =>
        {
            _settings.VideoFollowsWidget = follow.IsChecked;
            SettingsChanged?.Invoke();
        };
        Menu.Items.Add(follow);

        var hide = new MenuItem { Header = "Hide the player" };
        hide.Click += (_, _) => Hide();
        Menu.Items.Add(hide);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    // Ctrl is required so that an ordinary scroll over the bar doesn't resize
    // the window by surprise, and the step is a fixed nudge rather than a
    // percentage, which ran away at larger sizes.
    private void Bar_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var step = e.Delta > 0 ? 24 : -24;
        var aspect = Height / Math.Max(1, Width);
        Width = Math.Round(Math.Clamp(Width + step, MinWidth, 1920));
        Height = Math.Round(Math.Clamp(Width * aspect, MinHeight, 1200));
        e.Handled = true;
    }

    /// <summary>
    /// Hand the drag to Windows rather than resizing by hand. Tracking it here
    /// meant each DragDelta was measured from a grip that the previous resize
    /// had just moved, so the deltas compounded and the window shot about.
    /// </summary>
    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        e.Handled = true;
        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, (IntPtr)HTBOTTOMRIGHT, IntPtr.Zero);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    /// <summary>
    /// Hold the window to the shape of what is playing while it is being
    /// dragged. Windows asks what the new rectangle should be through
    /// WM_SIZING, so the ratio is enforced during the drag rather than
    /// snapping back after it.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_SIZING || AspectLock is not double aspect || aspect <= 0.05) return IntPtr.Zero;

        var rect = Marshal.PtrToStructure<RECT>(lParam);
        var dpi = VisualTreeHelper.GetDpi(this);
        var chromeX = ChromeWidth * dpi.DpiScaleX;
        var chromeY = ChromeHeight * dpi.DpiScaleY;
        var edge = (int)wParam;

        if (edge is WMSZ_TOP or WMSZ_BOTTOM)
        {
            var content = Math.Max(1, rect.Bottom - rect.Top - chromeY);
            rect.Right = rect.Left + (int)Math.Round(content * aspect + chromeX);
        }
        else
        {
            var content = Math.Max(1, rect.Right - rect.Left - chromeX);
            var height = (int)Math.Round(content / aspect + chromeY);
            if (edge is WMSZ_TOPLEFT or WMSZ_TOPRIGHT) rect.Top = rect.Bottom - height;
            else rect.Bottom = rect.Top + height;
        }

        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return (IntPtr)1;
    }

    private void ReassertTopmost()
    {
        if (!IsVisible || !Topmost || _menuOpen || _interactive) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnClosed(EventArgs e)
    {
        IconProvider.SiteIconArrived -= OnIconArrived;
        _topmostTick.Stop();
        foreach (var tab in _tabs)
        {
            try { tab.View.Dispose(); } catch { }
        }
        _tabs.Clear();
        base.OnClosed(e);
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "...";

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

    private const int WM_SIZING = 0x0214;
    private const int WMSZ_TOP = 3;
    private const int WMSZ_TOPLEFT = 4;
    private const int WMSZ_TOPRIGHT = 5;
    private const int WMSZ_BOTTOM = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
