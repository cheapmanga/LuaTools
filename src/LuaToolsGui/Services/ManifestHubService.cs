using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// The free, account-free ManifestHub source: a branch archive per appid, carrying the lua and the
/// <c>.manifest</c> files that go with it, plus the public depot-key database the other free paths
/// build on.
/// </summary>
/// <remarks>
/// <para>Where lua.tools meters downloads at 25/day behind a Discord/Supabase account, this touches
/// neither. ManifestHub publishes one flat <c>depotkeys.json</c> - a public map of depot id → decryption
/// key - which every "free lua generator" site ultimately fronts. This reads it directly (once per
/// session, cached), asks <see cref="SteamDepotInfo"/> which depots a game has, and emits the
/// <c>addappid</c>/<c>setManifestid</c> lua the local installer already understands. No server of theirs,
/// no quota, no login.</para>
///
/// <para><b>Changed on 2026-09-09.</b> This used to serve the row by synthesising a lua from the key
/// database alone and letting Steam fetch the manifests. Steam then closed the route that served
/// manifests for apps you don't own, which left a keys-only source unable to install anything, and the
/// upstream repo has since purged its per-appid branches. The row now downloads a branch archive from a
/// community mirror of that same corpus - lua and manifests together, nothing to ask Steam for. The
/// snapshot is frozen at 2025-07-26, so it installs an old build of a game; that is the trade, and it
/// is the difference between an old build and none. The key database stays, because
/// <see cref="ManifestCacheService"/> and addon-contributed sources are built on it.</para>
///
/// <para>Coverage is whatever keys have been dumped: a depot with no key in the database is simply left
/// out, and a game with none is "not available here" - the caller then falls back to lua.tools. It is an
/// excellent primary source, not a guaranteed superset.</para>
/// </remarks>
public class ManifestHubService(GithubProxy gh, SteamDepotInfo depotInfo, SteamAppInfoCache appInfo, ILogger<ManifestHubService> log)
{

    /// <summary>The source name this appears under in the Add page's row list.</summary>
    public const string SourceName = "manifesthub";

    /// <summary>Every mirror's url for this game's branch archive, in preference order.</summary>
    private static IEnumerable<string> ZipUrls(long appId) =>
        AppConfig.ManifestHubZipUrls.Select(t => string.Format(t, appId));

    // A bare existence probe, with its own short timeout so it never inherits a download's. The real
    // fetch goes through GithubProxy for the usual mirror fallback.
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>depot id → decryption key (hex). Loaded once; null until the first successful load.</summary>
    private IReadOnlyDictionary<long, string>? _keys;

    /// <summary>
    /// Load the key database if it isn't in memory yet. Null when it couldn't be fetched (offline, etc.),
    /// which the caller reads as "the free source is unavailable right now", never as an error.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, string>?> EnsureKeysAsync(CancellationToken ct)
    {
        if (_keys is not null) return _keys;

        await _gate.WaitAsync(ct);
        try
        {
            if (_keys is not null) return _keys; // won the race

            var keys = await FetchKeyDatabaseAsync(AppConfig.ManifestHubKeysUrls, ct);
            return keys is null ? null : _keys = keys;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Load a flat <c>{"&lt;depotid&gt;": "&lt;key&gt;"}</c> database from the first of <paramref name="urls"/>
    /// that yields a usable one. Public because addon-contributed key databases are the same shape and
    /// deserve the same mirror fallback; the caching, which differs per source, stays with the caller.
    /// </summary>
    /// <remarks>
    /// The next url is tried only when the one before is unreachable or parses to nothing, so a frozen or
    /// DMCA'd upstream falls through to a fresher copy. Null means every url failed, which callers read as
    /// "this source is unavailable right now", never as an error.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, string>?> FetchKeyDatabaseAsync(
        IEnumerable<string> urls, CancellationToken ct = default)
    {
        foreach (var url in urls)
        {
            try
            {
                using var res = await gh.SendAsync(url, ct);
                if (res is null || !res.IsSuccessStatusCode)
                {
                    log.LogDebug("depotkeys.json fetch failed at {Url}: {Status}", url, res?.StatusCode);
                    continue; // try the next mirror
                }

                byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct);

                // Parse off the UI thread. This runs from a UI-thread command (Fetch → HasGameAsync),
                // and deserializing 15 MB into a ~200k-entry map is enough to hitch the window for a
                // moment on the first game of a session. The caller's own await still resumes on the UI
                // thread, so its ObservableCollection writes stay safe.
                var keys = await Task.Run(() =>
                {
                    // Flat {"<depotid>": "<key>"} object. Drop any entry whose id isn't a number
                    // rather than failing the whole load.
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes);
                    if (raw is null) return null;

                    var map = new Dictionary<long, string>(raw.Count);
                    foreach (var (id, key) in raw)
                        if (long.TryParse(id, out long depot) && !string.IsNullOrWhiteSpace(key))
                            map[depot] = key.Trim();
                    return map;
                }, ct);

                if (keys is { Count: > 0 }) return keys; // good copy → stop here
                log.LogDebug("depotkeys.json from {Url} parsed to nothing; trying the next mirror", url);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Includes an HttpClient timeout (surfaced as TaskCanceledException). Try the next
                // mirror; nothing is cached, so a later lookup retries from the top.
                log.LogDebug(ex, "Loading the depot-key database from {Url} failed", url);
            }
        }

        return null; // every mirror failed → "free source unavailable right now"
    }

    /// <summary>The cached key database, or null when it can't be fetched right now.</summary>
    /// <remarks>Public so <see cref="ManifestCacheService"/> pairs its manifests with these keys without
    /// re-downloading 15 MB of json per game: the cache lives here, one copy per session.</remarks>
    public Task<IReadOnlyDictionary<long, string>?> GetKeysAsync(CancellationToken ct = default) =>
        EnsureKeysAsync(ct);

    /// <summary>
    /// Does the corpus have a branch for this game? A HEAD that answers 200 on any mirror; 404
    /// everywhere (or a total failure) means no.
    /// </summary>
    /// <remarks>
    /// This asks the mirrors, not the key database. Since 2026-09-09 the row installs a branch archive,
    /// so what matters is whether the archive exists - a depot key with no manifest beside it no longer
    /// produces anything installable. An uncovered appid answers a clean 404, which is why this can be
    /// a plain existence check.
    /// </remarks>
    public async Task<bool> HasGameAsync(long appId, CancellationToken ct = default)
    {
        foreach (var url in ZipUrls(appId))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
                using var res = await _probe.SendAsync(req, ct);
                if (res.StatusCode == HttpStatusCode.OK) return true;
                if (res.StatusCode == HttpStatusCode.NotFound) return false; // the mirrors are identical
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "ManifestHub HEAD for {AppId} at {Url} failed", appId, url);
            }
        }

        return false;
    }

    /// <summary>Is at least one of this game's depots in the key database?</summary>
    /// <remarks>
    /// What <see cref="HasGameAsync"/> used to ask. It is no longer enough on its own to offer this row,
    /// but it is exactly the question <see cref="ManifestCacheService"/> has to answer before offering
    /// manifests it cannot decrypt.
    /// </remarks>
    public async Task<bool> HasKeysAsync(long appId, CancellationToken ct = default)
    {
        var keys = await EnsureKeysAsync(ct);
        if (keys is null) return false;

        var info = await depotInfo.GetAsync(appId, ct);
        if (info is null) return false;

        return info.Depots.Any(d => keys.ContainsKey(d.Id));
    }

    /// <summary>
    /// Download the game's branch archive to a temp file, for the install pipeline to unpack.
    /// </summary>
    /// <remarks>
    /// Mirrors are tried in order and every one holds the same bytes, so the first that answers wins.
    /// The archive has a root folder whose name varies per mirror (<c>ManifestHub3-&lt;appid&gt;/</c>,
    /// <c>ManifestHub2-&lt;appid&gt;/</c>); nothing here needs to care, because the installer keys off each
    /// entry's leaf name and ignores anything that isn't a .lua or a .manifest - key.vdf and the json
    /// included.
    /// </remarks>
    public async Task<DownloadedFile> DownloadZipAsync(
        long appId, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifesthub-{appId}.zip");

        var sink = progress is null ? null
            : new ProgressRelay<double?>(f => progress.Report(new DownloadProgress((long)((f ?? 0) * 1000), 1000)));

        foreach (var url in ZipUrls(appId))
        {
            try
            {
                await gh.DownloadAsync(url, path, sink, ct);
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return new DownloadedFile(path, $"{appId}.zip");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "ManifestHub zip for {AppId} at {Url} failed", appId, url);
            }
        }

        throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);
    }

    /// <summary>
    /// Build the game's lua from the public keys and write it to a temp file, for the install pipeline
    /// to pick up exactly as it would a downloaded one.
    /// </summary>
    /// <remarks>
    /// <para>The base game plus, like lua.tools, its DLCs and soundtracks. Each declared DLC gets an
    /// <c>addappid(&lt;dlcid&gt;)</c> entitlement line - many DLCs and most soundtracks are store-only with
    /// no depot, so the appid alone is what unlocks them. DLCs that DO carry content are covered the same
    /// way base content is: their depot appears in the depot list and gets its key.</para>
    ///
    /// <para>Every content depot with a key gets an <c>addappid(&lt;depot&gt;,1,"&lt;key&gt;")</c> line and, when
    /// known, a <c>setManifestid</c> pin. The installer comments those pins out under Auto Update, so
    /// including them costs nothing and gives a coherent pinned lua to anyone who turns it off.</para>
    /// </remarks>
    public async Task<DownloadedFile> BuildLuaAsync(long appId, CancellationToken ct = default)
    {
        var keys = await EnsureKeysAsync(ct)
            ?? throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        return await BuildLuaFromKeysAsync(appId, keys, ct);
    }

    /// <summary>
    /// Build a game's lua from an already-loaded depot-key map. Public so an addon-contributed key
    /// database produces byte-for-byte the same lua as the built-in source: the DLC and soundtrack
    /// unlocks, the manifest pins and the de-duplication are subtle enough that a second implementation
    /// would drift, and every drift would be a game that installs differently depending on which source
    /// the user happened to pick.
    /// </summary>
    public Task<DownloadedFile> BuildLuaFromKeysAsync(
        long appId, IReadOnlyDictionary<long, string> keys, CancellationToken ct = default) =>
        BuildLuaAsync(appId, keys, pins: null, ct);

    /// <summary>
    /// Build a lua pinned to manifests we already hold, rather than to whatever build Steam serves today.
    /// </summary>
    /// <remarks>
    /// <para>This is what makes a manifests-only corpus usable. <paramref name="pins"/> maps a depot id to
    /// the manifest id of a file that is about to be written into depotcache, read off the file's own
    /// name. Pinning to those means Steam never has to ask for a manifest, which is the whole point since
    /// 2026-09-09.</para>
    ///
    /// <para>A depot is emitted only when it has BOTH a key and a manifest here. Emitting a keyed depot
    /// with no local manifest would send Steam back down the closed route and stall the download; leaving
    /// it out means Steam never asks for it. The cost is an install that can be missing a depot, which is
    /// visible and recoverable, rather than one that hangs at zero bytes.</para>
    /// </remarks>
    public Task<DownloadedFile> BuildLuaForManifestsAsync(
        long appId, IReadOnlyDictionary<long, string> keys,
        IReadOnlyDictionary<long, string> pins, CancellationToken ct = default) =>
        BuildLuaAsync(appId, keys, pins, ct);

    private async Task<DownloadedFile> BuildLuaAsync(
        long appId, IReadOnlyDictionary<long, string> keys,
        IReadOnlyDictionary<long, string>? pins, CancellationToken ct)
    {
        var info = await depotInfo.GetAsync(appId, ct)
            ?? throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        var keyed = info.Depots
            .Where(d => keys.ContainsKey(d.Id) && (pins is null || pins.ContainsKey(d.Id)))
            .ToList();
        if (keyed.Count is 0)
            throw new DownloadAbortedException(Resources.Strings.Free_Err_NoKeys);

        var lua = new StringBuilder();
        var addedApps = new HashSet<long>();

        // An addappid line, emitted once per appid. Steam ignores a duplicate but the diff shouldn't show one.
        void AddApp(long id)
        {
            if (addedApps.Add(id))
                lua.Append("addappid(").Append(id).Append(")\n");
        }

        AddApp(appId);

        // DLCs and soundtracks: unlock every declared entitlement, with or without a depot. This is what
        // lua.tools does, and it's the only thing store-only DLCs and soundtracks need. Two sources unioned
        // (AddApp de-dups): appinfo's listofdlc, plus the store's dlc list — the latter is what carries a
        // dedicated Steam Soundtrack (music) app, which listofdlc omits. The id > 0 guard shrugs off a
        // malformed entry rather than emitting addappid(0).
        var storeDlc = await appInfo.GetStoreDlcIdsAsync(appId, ct);
        foreach (var dlc in info.DlcIds.Concat(storeDlc).Where(id => id > 0))
            AddApp(dlc);

        // Content depots with a key - the base game's and any DLC's alike (DLC depots are in this list too).
        foreach (var d in keyed)
        {
            lua.Append("addappid(").Append(d.Id).Append(",1,\"").Append(keys[d.Id]).Append("\")\n");

            // The manifest we hold wins over the one Steam is serving today: it is the one that will be
            // on disk. Without pins, this falls back to appinfo's current public gid as before.
            string? gid = pins is not null ? pins[d.Id] : d.PublicManifestId;
            if (!string.IsNullOrWhiteSpace(gid))
                lua.Append("setManifestid(").Append(d.Id).Append(",\"").Append(gid).Append("\",0)\n");
        }

        string path = Path.Combine(Path.GetTempPath(), $"{appId}.lua");
        await File.WriteAllTextAsync(path, lua.ToString(), new UTF8Encoding(false), ct);

        return new DownloadedFile(path, $"{appId}.lua");
    }
}
