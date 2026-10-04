using System.Net.Http;
using System.Text.Json;

namespace PlayMe;

/// <summary>
/// Fallback album art for players that publish no SMTC thumbnail (the Amazon
/// Music desktop app doesn't): look the track up on the keyless iTunes Search
/// API and use its artwork. Results (including misses) are cached per track.
/// </summary>
public sealed class ArtFetcher
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<string, byte[]?> _cache = new();

    public async Task<byte[]?> TryFetchAsync(string artist, string title)
    {
        var key = artist + "|" + title;
        if (key == "|") return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (_cache.Count > 200) _cache.Clear();
        }

        byte[]? art = null;
        try
        {
            var term = Uri.EscapeDataString($"{artist} {title}".Trim());
            var url = $"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=1";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var results = doc.RootElement.GetProperty("results");
            if (results.GetArrayLength() > 0 &&
                results[0].TryGetProperty("artworkUrl100", out var artUrl) &&
                artUrl.GetString() is { } small)
            {
                art = await _http.GetByteArrayAsync(small.Replace("100x100", "600x600"));
            }
        }
        catch { }

        lock (_cache) _cache[key] = art;
        return art;
    }
}
