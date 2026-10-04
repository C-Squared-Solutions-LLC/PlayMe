using System.Text.RegularExpressions;

namespace PlayMe;

/// <summary>A site the player can open.</summary>
public sealed record Site(string Title, string Url, string Domain);

/// <summary>
/// Turns whatever the user types - or whatever a window title hints at - into
/// something the player can open, and keeps the handful of starting points
/// that are worth offering out of the box.
/// </summary>
public static class SiteCatalog
{
    public static readonly Site[] Defaults =
    {
        new("Amazon Music", "https://music.amazon.com", "music.amazon.com"),
        new("Twitch", "https://www.twitch.tv", "twitch.tv"),
        new("YouTube", "https://www.youtube.com", "youtube.com"),
    };

    /// <summary>
    /// Accepts a URL, a bare domain, or a Twitch channel name, and returns
    /// something navigable - or null if it can't make sense of it.
    /// </summary>
    public static Site? Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        // A bare word is almost always a Twitch channel in this context.
        if (!text.Contains('.') && !text.Contains('/') && !text.Contains(' '))
            return new Site(text, $"https://www.twitch.tv/{text.ToLowerInvariant()}", "twitch.tv");

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps &&
            uri.Scheme != Uri.UriSchemeFile) return null;

        var domain = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        if (domain.Length == 0) domain = "file";
        return new Site(TitleFor(uri, domain), uri.ToString(), domain);
    }

    private static string TitleFor(Uri uri, string domain)
    {
        var path = uri.AbsolutePath.Trim('/');
        // twitch.tv/somechannel reads better as "somechannel".
        if (domain.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) && path.Length > 0)
            return path.Split('/')[0];
        return domain;
    }

    /// <summary>
    /// Sites worth suggesting because they are already open somewhere: the
    /// Twitch channel you are watching, the site a window title names.
    /// </summary>
    public static List<Site> Detected(IEnumerable<CaptureTarget> windows)
    {
        var found = new List<Site>();
        foreach (var window in windows)
        {
            var site = IconProvider.SiteFor(window.Title);
            if (site is null) continue;

            var url = $"https://{site.Value.Domain}";
            var title = site.Value.Name;

            // "<channel> - Twitch - Google Chrome" tells us the channel.
            if (site.Value.Domain.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                var channel = TwitchChannel(window.Title);
                if (channel is not null)
                {
                    url = $"https://www.twitch.tv/{channel}";
                    title = channel;
                }
            }

            if (found.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(new Site(title, url, site.Value.Domain));
        }
        return found;
    }

    private static readonly Regex TwitchTitle =
        new(@"^(?<channel>[A-Za-z0-9_]{3,25})\s*[-–]\s*Twitch", RegexOptions.IgnoreCase);

    private static string? TwitchChannel(string windowTitle)
    {
        var match = TwitchTitle.Match(windowTitle.Trim());
        return match.Success ? match.Groups["channel"].Value.ToLowerInvariant() : null;
    }
}
