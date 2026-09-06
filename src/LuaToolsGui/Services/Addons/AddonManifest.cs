using System.Text.Json.Serialization;

namespace LuaToolsGui.Services.Addons;

/// <summary>
/// <c>addon.json</c>, the one file every addon must have. Deliberately readable and hand-writable: the
/// whole payload is a few lines of JSON contributing a manifest source, with nothing compiled and
/// nothing to trust beyond a URL. There is deliberately no way for an addon to carry code.
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
