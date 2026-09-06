using System.Text.Json.Serialization;

namespace LuaToolsGui.Services.Addons;

/// <summary>
/// <c>addon.json</c>, the one file every addon must have. Deliberately readable and hand-writable: the
/// cheapest useful addon is a few lines of JSON contributing a manifest source, with nothing compiled
/// and nothing to trust beyond a URL.
/// </summary>
public sealed class AddonManifest
{
    /// <summary>
    /// Format version of this file, not of the addon. Bumped only when a change would make an older
    /// host misread a newer manifest; a host refuses a schema from the future rather than guess at it.
    /// </summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// <summary>Stable identity. Must equal the folder name, so the id can't be spoofed by editing the file alone.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Lowest host version this addon works against. An addon built for a newer LuaTools is skipped with
    /// a message telling the user to update, which is a far better failure than a MissingMethodException
    /// from inside a page.
    /// </summary>
    [JsonPropertyName("minHostVersion")]
    public string? MinHostVersion { get; set; }

    /// <summary>
    /// File name (not a path) of the addon assembly, relative to the addon folder. Null or empty means a
    /// data-only addon: nothing is loaded, nothing executes, and the manifest below is the whole payload.
    /// </summary>
    [JsonPropertyName("assembly")]
    public string? Assembly { get; set; }

    /// <summary>
    /// Hex sha256 of <see cref="Assembly"/>, required whenever one is named. Not a trust decision — a
    /// hash an attacker can rewrite alongside the file proves nothing on its own. It exists so the
    /// catalog entry, the published addon and the bytes on disk can be shown to be the same thing,
    /// which is what makes "it's open source" checkable rather than merely true.
    /// </summary>
    [JsonPropertyName("assemblySha256")]
    public string? AssemblySha256 { get; set; }

    /// <summary>Manifest sources contributed as pure data. Available to data-only addons.</summary>
    [JsonPropertyName("sources")]
    public List<AddonSourceEntry> Sources { get; set; } = [];

    /// <summary>Where this addon came from, for the Addons page to show. Informational.</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }
}

/// <summary>
/// The JSON form of a <see cref="LuaTools.Addons.ManifestSourceDescriptor"/>. Kept as its own type so
/// the wire format can stay frozen while the contract type is free to grow.
/// </summary>
public sealed class AddonSourceEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>"manifestZip" | "luaFile" | "depotKeyDatabase", case-insensitive.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("mirrors")]
    public List<string> Mirrors { get; set; } = [];

    [JsonPropertyName("order")]
    public int Order { get; set; } = 200;

    [JsonPropertyName("badge")]
    public string? Badge { get; set; }

    [JsonPropertyName("free")]
    public bool Free { get; set; } = true;
}
