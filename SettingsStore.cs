using System.IO;
using System.Text.Json;

namespace PlayMe;

public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; } = true;

    // Volume level the quiet button jumps to (0.0 - 1.0).
    public double QuietLevel { get; set; } = 0.10;

    // Player panel geometry. (The Video* names are kept so an existing
    // settings.json doesn't lose where the panel was put.)
    public bool VideoEnabled { get; set; }
    public double? VideoLeft { get; set; }
    public double? VideoTop { get; set; }
    public double VideoWidth { get; set; } = 420;
    public double VideoHeight { get; set; } = 290;
    public bool VideoFollowsWidget { get; set; } = true;

    // What the player is showing, and the favourites strip. The collections
    // tolerate a null in the file - a hand-edited settings.json shouldn't be
    // able to take the whole app down.
    public string? PlayerUrl { get; set; }
    public double PlayerVolume { get; set; } = 1.0;

    // Crop the picture to fill the panel, rather than shaping the panel to
    // match what is playing.
    public bool PlayerFill { get; set; }
    public bool PlayerMuted { get; set; }

    private List<string> _playerSites = new();
    public List<string> PlayerSites
    {
        get => _playerSites;
        set => _playerSites = value ?? new List<string>();
    }

    // Domains to show cropped to just their video, rather than the whole page.
    private List<string> _playerVideoOnly = new();
    public List<string> PlayerVideoOnly
    {
        get => _playerVideoOnly;
        set => _playerVideoOnly = value ?? new List<string>();
    }

    // Mixer panel: per-app volume for everything that makes sound.
    public bool MixerEnabled { get; set; }
    public double? MixerLeft { get; set; }
    public double? MixerTop { get; set; }

    // Apps turned up past 100%, keyed by exe path.
    private Dictionary<string, double> _boostLevels = new();
    public Dictionary<string, double> BoostLevels
    {
        get => _boostLevels;
        set => _boostLevels = value ?? new Dictionary<string, double>();
    }

    // Mixer level to hand back to a boosted app, so a crash can't leave one
    // parked at the boost leak level.
    private Dictionary<string, double> _boostRestore = new();
    public Dictionary<string, double> BoostRestore
    {
        get => _boostRestore;
        set => _boostRestore = value ?? new Dictionary<string, double>();
    }
}

public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayMe");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Unreadable settings used to be overwritten on the spot, which
            // threw away positions and favourites over one bad character.
            // Keep the original around instead.
            try
            {
                if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bad", true);
            }
            catch { }
        }
        var fresh = new Settings();
        Save(fresh); // write a template so the config keys are discoverable
        return fresh;
    }

    public static void Save(Settings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Options));
        }
        catch { }
    }
}
