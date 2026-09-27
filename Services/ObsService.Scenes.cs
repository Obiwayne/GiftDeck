using System.Text.Json;

namespace GiftDeck.Services;

public record ObsScene(string Name, string Uuid);
public record ObsTransition(string Name, string Kind, bool Fixed);
public record ObsSceneItem(int Id, string SourceName, string SourceUuid, string InputKind, bool IsGroup, bool Enabled, bool Locked);
public record ObsAudioInput(string Name, string Uuid, string Kind, double VolumeDb, bool Muted);

// Scenes page and audio mixer: everything here works on OBS's main canvas (plain obs-websocket requests).
public partial class ObsService
{
    // Top of OBS's scene list first (OBS itself lists them bottom to top), plus the scene on program now.
    public async Task<(List<ObsScene> Scenes, string ProgramUuid)> GetSceneListAsync()
    {
        var r = await RequestAsync("GetSceneList");
        var list = r.GetProperty("scenes").EnumerateArray()
            .Select(s => new ObsScene(J.Str(s, "sceneName"), J.Str(s, "sceneUuid")))
            .ToList();
        list.Reverse();
        return (list, J.Str(r, "currentProgramSceneUuid"));
    }

    public Task SwitchSceneAsync(string sceneUuid) => RequestAsync("SetCurrentProgramScene", new { sceneUuid });

    // Duration is null for fixed transitions such as Cut.
    public async Task<(List<ObsTransition> Transitions, string Current, int? DurationMs)> GetTransitionsAsync()
    {
        var r = await RequestAsync("GetSceneTransitionList");
        var list = r.GetProperty("transitions").EnumerateArray()
            .Select(t => new ObsTransition(J.Str(t, "transitionName"), J.Str(t, "transitionKind"), J.Bool(t, "transitionFixed")))
            .ToList();
        var cur = await RequestAsync("GetCurrentSceneTransition");
        int? duration = cur.TryGetProperty("transitionDuration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null;
        return (list, J.Str(cur, "transitionName"), duration);
    }

    public Task SetTransitionAsync(string transitionName) => RequestAsync("SetCurrentSceneTransition", new { transitionName });

    // OBS accepts 50 to 20000 ms.
    public Task SetTransitionDurationAsync(int ms) =>
        RequestAsync("SetCurrentSceneTransitionDuration", new { transitionDuration = Math.Clamp(ms, 50, 20000) });

    // Layers of a scene, top layer first like OBS's Sources list.
    public async Task<List<ObsSceneItem>> GetSceneItemsAsync(string sceneUuid)
    {
        var r = await RequestAsync("GetSceneItemList", new { sceneUuid });
        var list = r.GetProperty("sceneItems").EnumerateArray()
            .OrderByDescending(i => i.TryGetProperty("sceneItemIndex", out var x) ? x.GetInt32() : 0)
            .Select(i => new ObsSceneItem(
                i.GetProperty("sceneItemId").GetInt32(),
                J.Str(i, "sourceName"),
                J.Str(i, "sourceUuid"),
                J.Str(i, "inputKind"),
                J.Bool(i, "isGroup"),
                J.Bool(i, "sceneItemEnabled"),
                J.Bool(i, "sceneItemLocked")))
            .ToList();
        return list;
    }

    public Task SetSceneItemEnabledAsync(string sceneUuid, int sceneItemId, bool enabled) =>
        RequestAsync("SetSceneItemEnabled", new { sceneUuid, sceneItemId, sceneItemEnabled = enabled });

    // Every input that carries audio (mics, desktop audio, media, browser sources ...), with volume and mute.
    public async Task<List<ObsAudioInput>> GetAudioInputsAsync()
    {
        var r = await RequestAsync("GetInputList");
        var result = new List<ObsAudioInput>();
        foreach (var i in r.GetProperty("inputs").EnumerateArray())
        {
            // inputKindCaps bit 2 is OBS_SOURCE_AUDIO; older obs-websocket builds don't send caps, so then just ask.
            if (i.TryGetProperty("inputKindCaps", out var caps) && caps.ValueKind == JsonValueKind.Number && (caps.GetInt32() & 2) == 0) continue;
            var uuid = J.Str(i, "inputUuid");
            try
            {
                var vol = await RequestAsync("GetInputVolume", new { inputUuid = uuid });
                var mute = await RequestAsync("GetInputMute", new { inputUuid = uuid });
                result.Add(new ObsAudioInput(J.Str(i, "inputName"), uuid, J.Str(i, "inputKind"),
                    vol.GetProperty("inputVolumeDb").GetDouble(), J.Bool(mute, "inputMuted")));
            }
            catch (Exception e) when (e.Message.Contains("does not support audio", StringComparison.OrdinalIgnoreCase)) { }
        }
        return result;
    }

    // OBS's range is -100 dB (silent) to +26 dB.
    public Task SetInputVolumeDbAsync(string inputUuid, double db) =>
        RequestAsync("SetInputVolume", new { inputUuid, inputVolumeDb = Math.Clamp(db, -100, 26) });

    public Task SetInputMuteAsync(string inputUuid, bool muted) => RequestAsync("SetInputMute", new { inputUuid, inputMuted = muted });

    // InputVolumeMeters event data -> loudest peak of any channel per input uuid, in dB (-100 for silence).
    // Each channel is [magnitude, peak, input peak] as linear multipliers.
    public static Dictionary<string, double> ParseVolumeMeters(JsonElement data)
    {
        var levels = new Dictionary<string, double>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("inputs", out var inputs)) return levels;
        foreach (var input in inputs.EnumerateArray())
        {
            double peak = 0;
            if (input.TryGetProperty("inputLevelsMul", out var channels))
                foreach (var ch in channels.EnumerateArray())
                    if (ch.GetArrayLength() > 1) peak = Math.Max(peak, ch[1].GetDouble());
            var uuid = J.Str(input, "inputUuid");
            if (!string.IsNullOrEmpty(uuid)) levels[uuid] = peak > 0.00001 ? Math.Max(-100, 20 * Math.Log10(peak)) : -100;
        }
        return levels;
    }

    // Main canvas size, so thumbnails get the right shape (portrait in v2).
    public async Task<(int Width, int Height)> GetCanvasSizeAsync()
    {
        var r = await RequestAsync("GetVideoSettings");
        return (r.GetProperty("baseWidth").GetInt32(), r.GetProperty("baseHeight").GetInt32());
    }
}

// JSON helpers for this file only (other ObsService parts may have their own).
file static class J
{
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
