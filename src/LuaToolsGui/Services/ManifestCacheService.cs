using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// The free source that is both current and complete: manifests from SteamManifestCache, keys from
/// ManifestHub's database, assembled here into one installable zip.
/// </summary>
/// <remarks>
/// <para>Neither half installs anything alone. SteamManifestCache publishes a branch per appid holding
/// only <c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c> files and states plainly that it carries no
/// decryption keys. <see cref="ManifestHubService"/>'s <c>depotkeys.json</c> is the opposite: keys and
/// never a manifest, which stopped being installable on 2026-09-09 when Steam closed the route that
/// served manifests for apps you don't own.</para>
///
/// <para>Put together they are a whole source, and a fresh one - that corpus is still being fed, where
/// every ManifestHub mirror froze in July 2025. A manifest's file name IS the pair the lua needs: the
/// depot id and the manifest id. So the lua is written pinned to the exact files this zip is about to
/// drop into depotcache, and Steam is never asked for one.</para>
///
/// <para>The lua is injected into the downloaded archive rather than installed separately, so this rides
/// the same install path as every other zip source: one confirmation, one diff overlay, one place where
/// a lua ever reaches stplug-in.</para>
/// </remarks>
public partial class ManifestCacheService(GithubProxy gh, ManifestHubService hub, ILogger<ManifestCacheService> log)
{
    /// <summary>The source name this appears under in the Add page's row list.</summary>
    public const string SourceName = "manifestcache";

    private static string ZipUrl(long appId) => string.Format(AppConfig.ManifestCacheZipUrl, appId);

    // Existence probe only, with its own short timeout so it never inherits a download's.
    private readonly HttpClient _probe = AppHttp.Create(TimeSpan.FromSeconds(15));

    /// <summary>A depotcache file name: <c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c>.</summary>
    [GeneratedRegex(@"^(\d+)_(\d+)\.manifest$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestNameRegex();

    /// <summary>
    /// Does this source cover the game? Both halves have to be there: a branch in the manifest corpus,
    /// and at least one depot key to decrypt what it contains.
    /// </summary>
    /// <remarks>
    /// The key half is checked first because it is cached after the first game of the session, while the
    /// branch probe is a network round trip every time. An uncovered appid answers a clean 404.
    /// </remarks>
    public async Task<bool> HasGameAsync(long appId, CancellationToken ct = default)
    {
        if (!await hub.HasKeysAsync(appId, ct)) return false;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, ZipUrl(appId));
            req.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
            using var res = await _probe.SendAsync(req, ct);
            return res.StatusCode == HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "SteamManifestCache HEAD for {AppId} failed", appId);
            return false;
        }
    }

    /// <summary>
    /// Download the manifest branch, write the matching lua into it, and hand the whole thing to the
    /// install pipeline.
    /// </summary>
    public async Task<DownloadedFile> DownloadAsync(
        long appId, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifestcache-{appId}.zip");

        var sink = progress is null ? null
            : new ProgressRelay<double?>(f => progress.Report(new DownloadProgress((long)((f ?? 0) * 1000), 1000)));

        await gh.DownloadAsync(ZipUrl(appId), path, sink, ct);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        var pins = ReadPins(path);
        if (pins.Count is 0)
            throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        var keys = await hub.GetKeysAsync(ct)
            ?? throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        var lua = await hub.BuildLuaForManifestsAsync(appId, keys, pins, ct);
        InjectLua(path, lua.FilePath, appId);

        return new DownloadedFile(path, $"{appId}.zip");
    }

    /// <summary>
    /// depot id → manifest id, read off the archive's file names.
    /// </summary>
    /// <remarks>
    /// The names are the only metadata this corpus ships, and they are enough. Entries are matched on
    /// their leaf name because every branch archive nests everything under a folder named after the repo
    /// and the appid. A depot listed twice keeps its first entry: the corpus does not say which of two
    /// builds is newer, and a manifest id is not ordered, so picking arbitrarily would install a
    /// different build depending on zip order.
    /// </remarks>
    private Dictionary<long, string> ReadPins(string zipPath)
    {
        var pins = new Dictionary<long, string>();

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var m = ManifestNameRegex().Match(entry.Name);
                if (!m.Success) continue;
                if (!long.TryParse(m.Groups[1].Value, out long depot)) continue;

                if (!pins.TryAdd(depot, m.Groups[2].Value))
                    log.LogDebug("Depot {Depot} appears twice in the archive; keeping {Kept}", depot, pins[depot]);
            }
        }
        catch (Exception ex)
        {
            // A truncated or non-zip download reads as "no manifests here", which the caller reports as
            // the source being unavailable. It must never throw a raw parse error at the user.
            log.LogDebug(ex, "Reading manifest names from {Path} failed", zipPath);
        }

        return pins;
    }

    /// <summary>Add the built lua to the archive as <c>&lt;appid&gt;.lua</c>.</summary>
    /// <remarks>
    /// Any lua already in there is deleted first. This corpus ships none today, but two of them would
    /// leave the installer picking whichever came last in the central directory, and the one that must
    /// win is the one written against these manifests.
    /// </remarks>
    private static void InjectLua(string zipPath, string luaPath, long appId)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);

        foreach (var stale in archive.Entries
                     .Where(e => e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                     .ToList())
            stale.Delete();

        archive.CreateEntryFromFile(luaPath, $"{appId}.lua", CompressionLevel.Optimal);
    }
}
