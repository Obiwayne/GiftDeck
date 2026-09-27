using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GiftDeck.Services;

// Packs\<id>\pack.json: one supported game, how to find it, which mods to install and how. See docs/v2.1/plan.md.
public class GamePack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Short { get; set; } = "";           // one line under the name on the game card
    public string Description { get; set; } = "";
    public string Cover { get; set; } = "cover.png";
    public string Warning { get; set; } = "";         // shown in amber on the page, e.g. "Story Mode only"
    public List<PackDetect> Detect { get; set; } = new List<PackDetect>();
    public string Exe { get; set; } = "";             // must be in the game folder: how a folder is recognised
    public List<string> MustBeClosed { get; set; } = new List<string>();
    public List<PackRequirement> Requirements { get; set; } = new List<PackRequirement>();
    public List<PackComponent> Components { get; set; } = new List<PackComponent>();
    public List<PackEdit> Edits { get; set; } = new List<PackEdit>();
    public List<string> Presets { get; set; } = new List<string>();
    public string Commands { get; set; } = "";
    public List<string> Targets { get; set; } = new List<string>();

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public string CoverPath => string.IsNullOrWhiteSpace(Cover) ? null : Path.Combine(Dir, Cover);
}

// Where to look for the game: {"type":"steam","appId":271590}, {"type":"epic","appName":"…"},
// {"type":"registry","key":"HKLM\\…","value":"InstallFolder"}.
public class PackDetect
{
    public string Type { get; set; } = "";
    public int AppId { get; set; }
    public string AppName { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

// A line on the "Before you install" checklist. In pack.json either a plain string or
// {"text":"…","detail":"…","check":"closed"} (check: "closed" = the mustBeClosed processes aren't running,
// "found" = the game folder is known; anything else is for the user to tick off themselves).
[JsonConverter(typeof(PackRequirementConverter))]
public class PackRequirement
{
    public string Text { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Check { get; set; } = "";
}

public class PackRequirementConverter : JsonConverter<PackRequirement>
{
    public override PackRequirement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new PackRequirement { Text = reader.GetString() };
        using var doc = JsonDocument.ParseValue(ref reader);
        var r = new PackRequirement();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
            switch (p.Name.ToLowerInvariant())
            {
                case "text": r.Text = v; break;
                case "detail": r.Detail = v; break;
                case "check": r.Check = v; break;
            }
        }
        return r;
    }

    public override void Write(Utf8JsonWriter writer, PackRequirement value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("text", value.Text);
        writer.WriteString("detail", value.Detail);
        writer.WriteString("check", value.Check);
        writer.WriteEndObject();
    }
}

// One mod (or other download) the pack installs into the game folder.
public class PackComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string License { get; set; } = "";
    public PackSource Source { get; set; }
    public List<PackSource> Sources { get; set; }     // tried in order; "source" alone is the same as a list of one
    public string Extract { get; set; } = "zip";       // zip | none (a single file download)
    public List<PackFileMap> Files { get; set; } = new List<PackFileMap>();
    public List<string> Exclude { get; set; } = new List<string>(); // file name patterns never copied, e.g. "*.pdb"
    public string DetectInstalled { get; set; } = "";  // file in the game folder that shows it's there (put by anyone)
    public string VersionFile { get; set; } = "";      // in the download: a .txt whose first line is the version, or a .dll/.asi with a file version

    [JsonIgnore] public List<PackSource> AllSources => Sources is { Count: > 0 } ? Sources : Source != null ? new List<PackSource> { Source } : new List<PackSource>();
}

// Where a component comes from.
//   github: "repo", "asset" (regex on the asset name); the latest release.
//   url:    "url", or "page" + "find" (regex whose first group is the download link on that page); optional "sha256".
//           Sends the page as Referer (some sites refuse downloads without it).
//   local:  "path" to a folder or a .zip (environment variables allowed), for development builds of our own mods.
//   page:   "page" only: the user downloads it in their browser and picks the file in GiftDeck.
// "version" is a regex on the downloaded file name whose first group is the version.
// "instructions" tells the user what to download when GiftDeck can't do it itself.
public class PackSource
{
    public string Type { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Asset { get; set; } = "";
    public string Url { get; set; } = "";
    public string Page { get; set; } = "";
    public string Find { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string Instructions { get; set; } = "";
}

// "from" is inside the download: "*" (everything), "folder/" (that folder, keeping its layout),
// "*.dll" or "bin/*.dll" (matching files in that folder), or one file. "to" is inside the game folder;
// ending in "/" (or empty) means "into this folder".
public class PackFileMap
{
    public string From { get; set; } = "*";
    public string To { get; set; } = "";
}

// A setting changed after the files are copied. type "json": "path" is dot-separated (a.b.c), "value" any JSON.
// type "ini": "section" and "key", "value" as text.
public class PackEdit
{
    public string File { get; set; } = "";
    public string Type { get; set; } = "json";
    public string Path { get; set; } = "";
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public JsonNode Value { get; set; }
}

// %APPDATA%\GiftDeck\packs\<id>\state.json: what the user chose for this pack.
public class PackState
{
    public string GameFolder { get; set; } = "";
    public Dictionary<string, string> PresetProfiles { get; set; } = new Dictionary<string, string>(); // preset file -> profile made from it
}

// %APPDATA%\GiftDeck\packs\<id>\installed.json: what GiftDeck put in the game folder, so it can take it out again.
public class PackInstallRecord
{
    public string PackId { get; set; } = "";
    public string GameFolder { get; set; } = "";
    public DateTime InstalledAt { get; set; }
    public Dictionary<string, InstalledComponent> Components { get; set; } = new Dictionary<string, InstalledComponent>();
    public List<InstalledFile> Files { get; set; } = new List<InstalledFile>();
    public List<string> Folders { get; set; } = new List<string>(); // folders GiftDeck created (removed again if empty)
    public bool Complete { get; set; }                             // false: the last install stopped part way
}

public class InstalledComponent
{
    public string Version { get; set; } = "";
    public string From { get; set; } = "";
    public DateTime InstalledAt { get; set; }
}

public class InstalledFile
{
    public string Path { get; set; } = "";        // relative to the game folder
    public string Component { get; set; } = "";
    public string Backup { get; set; }            // the file that was there before, relative to packs\<id>\; null = there wasn't one
}

// What the Games page shows for one pack.
public class PackStatus
{
    public string GameFolder { get; set; }
    public string FoundBy { get; set; } = "";     // "Steam", "Epic Games", "the Rockstar Games Launcher", "you"
    public bool GameFound => !string.IsNullOrEmpty(GameFolder);
    public List<string> Running { get; set; } = new List<string>();
    public PackInstallRecord Installed { get; set; }
    public List<ComponentStatus> Components { get; set; } = new List<ComponentStatus>();

    public bool AnyInstalledByUs => Installed != null && Installed.Files.Count > 0;
    public bool UpdateAvailable => Components.Any(c => c.UpdateAvailable || (AnyInstalledByUs && c.InstalledVersion == null));
}

public class ComponentStatus
{
    public PackComponent Component { get; set; }
    public string InstalledVersion { get; set; }  // by GiftDeck
    public bool OnDisk { get; set; }              // DetectInstalled file present (by GiftDeck or not)
    public string DiskVersion { get; set; }       // its file version, when it has one
    public string LatestVersion { get; set; }     // null = not checked or can't tell
    public bool UpdateAvailable => InstalledVersion != null && LatestVersion != null && LatestVersion != InstalledVersion;
}

public class PackPreset
{
    public string File { get; set; } = "";        // relative to the pack folder
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
}

// Thrown when a component has to be downloaded in the browser (the site refuses GiftDeck's download,
// or it's a "page" source). The Games page opens the page and lets the user pick the downloaded file.
public class ManualDownloadNeeded : Exception
{
    public PackComponent Component { get; }
    public string PageUrl { get; }
    public string Instructions { get; }

    public ManualDownloadNeeded(PackComponent component, string pageUrl, string instructions, string why)
        : base($"GiftDeck couldn't download {component.Name} by itself{(string.IsNullOrEmpty(why) ? "" : " (" + why.TrimEnd('.') + ")")}. Download it from its page, then pick the file.")
    {
        Component = component;
        PageUrl = pageUrl;
        Instructions = instructions;
    }
}
