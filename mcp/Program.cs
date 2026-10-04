using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayMe;

// PlayMe MCP server: newline-delimited JSON-RPC over stdio (MCP stdio
// transport), exposing the same media control the widget has. Talks to
// Windows directly (SMTC + WASAPI), so it works whether or not the widget
// is running, and shares the widget's settings.json source filter.

var media = new MediaController();
var volume = new AppVolumeController();
var settings = SettingsStore.Load();
await media.InitializeAsync();

var preDuck = -1f;

using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

string? line;
while ((line = await reader.ReadLineAsync()) != null)
{
    if (line.Trim().Length == 0) continue;
    JsonNode? msg;
    try { msg = JsonNode.Parse(line); } catch { continue; }
    var method = msg?["method"]?.GetValue<string>();
    var id = msg?["id"];
    if (msg is null || method is null || id is null) continue; // notification or garbage

    JsonNode? result = null;
    JsonObject? error = null;
    try
    {
        result = method switch
        {
            "initialize" => Initialize(msg),
            "ping" => new JsonObject(),
            "tools/list" => ToolsList(),
            "tools/call" => await ToolsCall(msg),
            _ => null,
        };
        if (result is null)
            error = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" };
    }
    catch (Exception ex)
    {
        error = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
    }

    var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
    if (error is not null) resp["error"] = error;
    else resp["result"] = result;
    await writer.WriteLineAsync(resp.ToJsonString());
}

return;

static JsonNode Initialize(JsonNode msg)
{
    var requested = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
    return new JsonObject
    {
        ["protocolVersion"] = requested,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "playme", ["version"] = "1.9.3" },
    };
}

static JsonObject Tool(string name, string description, JsonObject props, string[]? required = null)
{
    var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
    if (required is { Length: > 0 })
        schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
    return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
}

static JsonObject SourceProp() => new()
{
    ["source"] = new JsonObject
    {
        ["type"] = "string",
        ["description"] = "Optional stream to target: substring of the app id, title, or artist "
            + "(e.g. 'amazon', 'chrome'). Defaults to the currently selected stream.",
    },
};

static JsonObject With(JsonObject props, string name, JsonObject prop)
{
    props[name] = prop;
    return props;
}

static JsonNode ToolsList() => new JsonObject
{
    ["tools"] = new JsonArray(
        Tool("now_playing",
            "Current track, playback state, app volume, and every media stream Windows reports.",
            new JsonObject()),
        Tool("play_pause", "Toggle play/pause on a stream.", SourceProp()),
        Tool("next_track", "Skip to the next track.", SourceProp()),
        Tool("previous_track", "Go back to the previous track.", SourceProp()),
        Tool("seek", "Seek to a position in the current track.",
            With(SourceProp(), "seconds", new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Position in seconds from the start of the track",
            }),
            new[] { "seconds" }),
        Tool("set_volume", "Set the Windows mixer volume of the targeted stream's app.",
            With(SourceProp(), "percent", new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Volume 0-100",
            }),
            new[] { "percent" }),
        Tool("mute", "Mute, unmute, or toggle the targeted stream's app audio (does not pause it).",
            With(SourceProp(), "mode", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("on", "off", "toggle"),
                ["description"] = "Defaults to toggle",
            })),
        Tool("duck",
            "Duck the active stream's app volume to the configured quiet level, remembering the "
            + "previous volume. Pass restore=true to bring the previous volume back.",
            new JsonObject { ["restore"] = new JsonObject { ["type"] = "boolean" } })
    ),
};

async Task<JsonNode> ToolsCall(JsonNode msg)
{
    var p = msg["params"];
    var name = p?["name"]?.GetValue<string>() ?? "";
    var args = p?["arguments"];
    string text;
    var isError = false;

    var (ok, err) = SelectIfGiven(args);
    if (!ok)
    {
        text = err;
        isError = true;
    }
    else
    {
        switch (name)
        {
            case "now_playing":
                text = await NowPlaying();
                break;
            case "play_pause":
                media.TogglePlayPause();
                text = "play/pause sent to " + (media.ActiveAppId ?? "no stream");
                break;
            case "next_track":
                media.Next();
                text = "next sent to " + (media.ActiveAppId ?? "no stream");
                break;
            case "previous_track":
                media.Previous();
                text = "previous sent to " + (media.ActiveAppId ?? "no stream");
                break;
            case "seek":
            {
                var seconds = args?["seconds"]?.GetValue<double>();
                if (seconds is null) { text = "seconds is required"; isError = true; break; }
                media.SeekTo(TimeSpan.FromSeconds(seconds.Value));
                text = $"seek to {seconds:0}s sent";
                break;
            }
            case "set_volume":
            {
                var percent = args?["percent"]?.GetValue<double>();
                var proc = MediaController.ProcessNameForAppId(media.ActiveAppId);
                if (percent is null) { text = "percent is required"; isError = true; break; }
                if (proc is null) { text = "no active stream to target"; isError = true; break; }
                volume.SetVolume(proc, (float)(percent.Value / 100.0));
                text = $"{proc} volume set to {percent:0}%";
                break;
            }
            case "mute":
            {
                var mode = args?["mode"]?.GetValue<string>() ?? "toggle";
                var proc = MediaController.ProcessNameForAppId(media.ActiveAppId);
                if (proc is null) { text = "no active stream to target"; isError = true; break; }
                var state = volume.Get(proc);
                var target = mode switch
                {
                    "on" => true,
                    "off" => false,
                    _ => !state.Muted,
                };
                volume.SetMute(proc, target);
                text = proc + (target ? " muted" : " unmuted");
                break;
            }
            case "duck":
            {
                var restore = args?["restore"]?.GetValue<bool>() ?? false;
                var proc = MediaController.ProcessNameForAppId(media.ActiveAppId);
                if (proc is null) { text = "no active stream to target"; isError = true; break; }
                if (restore)
                {
                    if (preDuck >= 0)
                    {
                        volume.SetVolume(proc, preDuck);
                        text = $"{proc} volume restored to {Math.Round(preDuck * 100)}%";
                        preDuck = -1f;
                    }
                    else { text = "nothing to restore"; }
                }
                else
                {
                    preDuck = volume.Get(proc).Volume;
                    volume.SetVolume(proc, (float)settings.QuietLevel);
                    volume.SetMute(proc, false);
                    text = $"{proc} ducked to {Math.Round(settings.QuietLevel * 100)}% (was {Math.Round(preDuck * 100)}%)";
                }
                break;
            }
            default:
                text = "unknown tool: " + name;
                isError = true;
                break;
        }
    }

    var result = new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };
    if (isError) result["isError"] = true;
    return result;
}

(bool ok, string err) SelectIfGiven(JsonNode? args)
{
    var source = args?["source"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(source)) return (true, "");
    var streams = media.GetStreams();
    var match = streams.FirstOrDefault(s =>
        s.AppId.Contains(source, StringComparison.OrdinalIgnoreCase) ||
        s.Title.Contains(source, StringComparison.OrdinalIgnoreCase) ||
        s.Artist.Contains(source, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        return (false, $"no matching stream for '{source}'; available: " +
            (streams.Count == 0 ? "(none)" : string.Join(", ", streams.Select(s => $"{s.AppId} ({s.Title})"))));
    }
    media.SelectStream(match.Index);
    return (true, "");
}

async Task<string> NowPlaying()
{
    var track = await media.GetTrackAsync();
    var pb = media.GetPlayback();
    var tl = media.GetTimeline();
    var streams = media.GetStreams();
    var proc = MediaController.ProcessNameForAppId(media.ActiveAppId);
    var vol = volume.Get(proc);

    var o = new JsonObject
    {
        ["active"] = pb.HasSession
            ? new JsonObject
            {
                ["title"] = track?.Title ?? "",
                ["artist"] = track?.Artist ?? "",
                ["app"] = media.ActiveAppId ?? "",
                ["is_playing"] = pb.IsPlaying,
                ["position_seconds"] = Math.Round(tl.Position.TotalSeconds),
                ["duration_seconds"] = Math.Round(tl.Duration.TotalSeconds),
            }
            : null,
        ["streams"] = new JsonArray(streams.Select(s => (JsonNode)new JsonObject
        {
            ["index"] = s.Index,
            ["app"] = s.AppId,
            ["title"] = s.Title,
            ["artist"] = s.Artist,
            ["is_playing"] = s.IsPlaying,
            ["is_selected"] = s.IsSelected,
        }).ToArray()),
        ["volume"] = vol.Available
            ? new JsonObject
            {
                ["percent"] = Math.Round(vol.Volume * 100),
                ["muted"] = vol.Muted,
                ["app_process"] = proc,
            }
            : null,
    };
    return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
