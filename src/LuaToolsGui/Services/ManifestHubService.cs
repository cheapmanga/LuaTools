using System.IO;
using System.Text;
using System.Text.Json;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// The free, account-free manifest source: builds a game's lua from the public depot-key database
/// (SteamAutoCracks/ManifestHub) plus Steam's own depot list.
/// </summary>
/// <remarks>
/// <para>Where lua.tools meters downloads at 25/day behind a Discord/Supabase account, this touches
/// neither. ManifestHub publishes one flat <c>depotkeys.json</c> - a public map of depot id → decryption
/// key - which every "free lua generator" site ultimately fronts. This reads it directly (once per
/// session, cached), asks <see cref="SteamDepotInfo"/> which depots a game has, and emits the
/// <c>addappid</c>/<c>setManifestid</c> lua the local installer already understands. No server of theirs,
/// no quota, no login.</para>
///
/// <para>Coverage is whatever keys have been dumped: a depot with no key in the database is simply left
/// out, and a game with none is "not available here" - the caller then falls back to lua.tools. It is an
/// excellent primary source, not a guaranteed superset.</para>
/// </remarks>
public class ManifestHubService(GithubProxy gh, SteamDepotInfo depotInfo, SteamAppInfoCache appInfo, ILogger<ManifestHubService> log)
{
    /// <summary>Public raw URL of the key database. GithubProxy gives it the same mirror fallback as the rest.</summary>
    private const string DepotKeysUrl = "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json";

    /// <summary>The source name this appears under in the Add page's row list.</summary>
    public const string SourceName = "manifesthub";

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

            using var res = await gh.SendAsync(DepotKeysUrl, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                log.LogDebug("depotkeys.json fetch failed: {Status}", res?.StatusCode);
                return null;
            }

            byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct);

            // Parse off the UI thread. This runs from a UI-thread command (Fetch → HasGameAsync), and
            // deserializing 15 MB into a ~200k-entry map is enough to hitch the window for a moment on
            // the first game of a session. The caller's own await still resumes on the UI thread, so its
            // ObservableCollection writes stay safe.
            var keys = await Task.Run(() =>
            {
                // The file is a flat {"<depotid>": "<key>"} object. Drop any entry whose id isn't a
                // number rather than failing the whole load.
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes);
                if (raw is null) return null;

                var map = new Dictionary<long, string>(raw.Count);
                foreach (var (id, key) in raw)
                    if (long.TryParse(id, out long depot) && !string.IsNullOrWhiteSpace(key))
                        map[depot] = key.Trim();
                return map;
            }, ct);

            return keys is null ? null : _keys = keys;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Includes an HttpClient timeout (surfaced as TaskCanceledException). Not cached, so the
            // next lookup tries again.
            log.LogDebug(ex, "Loading the depot-key database failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Does the free source cover this game - i.e. is at least one of its depots in the key database?
    /// </summary>
    /// <remarks>
    /// Cheap after the first call: the keys are cached, and the depot list comes from SteamDepotInfo's
    /// own cache. A false here is why the Add page hides the ManifestHub row and points at lua.tools.
    /// </remarks>
    public async Task<bool> HasGameAsync(long appId, CancellationToken ct = default)
    {
        var keys = await EnsureKeysAsync(ct);
        if (keys is null) return false;

        var info = await depotInfo.GetAsync(appId, ct);
        if (info is null) return false;

        return info.Depots.Any(d => keys.ContainsKey(d.Id));
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

        var info = await depotInfo.GetAsync(appId, ct)
            ?? throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        var keyed = info.Depots.Where(d => keys.ContainsKey(d.Id)).ToList();
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
            if (!string.IsNullOrWhiteSpace(d.PublicManifestId))
                lua.Append("setManifestid(").Append(d.Id).Append(",\"").Append(d.PublicManifestId).Append("\",0)\n");
        }

        string path = Path.Combine(Path.GetTempPath(), $"{appId}.lua");
        await File.WriteAllTextAsync(path, lua.ToString(), new UTF8Encoding(false), ct);

        return new DownloadedFile(path, $"{appId}.lua");
    }
}
