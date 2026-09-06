using System.IO;
using System.Net;
using System.Net.Http;
using LuaTools.Addons;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services.Addons;

/// <summary>
/// Fetches from the manifest sources addons contribute. One service for every addon source, because a
/// descriptor names a SHAPE the host already knows how to consume rather than supplying a routine of its
/// own — which is what lets an addon with no code at all contribute a working source.
/// </summary>
/// <remarks>
/// Everything goes through <see cref="GithubProxy"/>, including the existence probe. That matters: the
/// proxy tries the url directly first and only then its mirrors, so a probe that skipped it would answer
/// "this source doesn't have the game" whenever GitHub itself was blocked, while the download that
/// followed would have succeeded through a mirror.
/// </remarks>
public class AddonSourceService(
    GithubProxy gh, ManifestHubService hub, SteamDepotInfo depotInfo, ILogger<AddonSourceService> log)
{
    // Key databases are big (ManifestHub's is ~15 MB) and serve every lookup for the session, so each
    // source's is loaded once. Only successes are cached: a failed load leaves nothing behind, so the
    // next game retries instead of inheriting one bad moment for the whole session.
    private readonly Dictionary<string, IReadOnlyDictionary<long, string>> _keyDbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    // Its own client so a probe never inherits a long download timeout.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Does this source cover the game? Any failure answers "no", never an error.</summary>
    public async Task<bool> HasGameAsync(ManifestSourceDescriptor source, long appId, CancellationToken ct = default)
    {
        try
        {
            if (source.Kind is ManifestSourceKind.DepotKeyDatabase)
            {
                var keys = await EnsureKeysAsync(source, ct);
                if (keys is null) return false;
                var info = await depotInfo.GetAsync(appId, ct);
                return info is not null && info.Depots.Any(d => keys.ContainsKey(d.Id));
            }

            foreach (string url in Urls(source, appId))
                foreach (string candidate in GithubProxy.Candidates(url))
                {
                    try
                    {
                        if (await ExistsAsync(candidate, ct)) return true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* this candidate is out; the next one may answer */ }
                }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Addon source {Source} probe for {AppId} failed", source.Name, appId);
            return false;
        }
    }

    /// <summary>
    /// Fetch the game from this source, as a file the existing install pipeline can take: a zip, a lua,
    /// or a lua synthesised from a key database.
    /// </summary>
    public async Task<DownloadedFile> FetchAsync(
        ManifestSourceDescriptor source, long appId, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (source.Kind is ManifestSourceKind.DepotKeyDatabase)
        {
            var keys = await EnsureKeysAsync(source, ct)
                ?? throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

            // Built by the same code the built-in free source uses, so which source the user picked can
            // never change how a game ends up installed.
            return await hub.BuildLuaFromKeysAsync(appId, keys, ct);
        }

        string ext = source.Kind is ManifestSourceKind.ManifestZip ? "zip" : "lua";
        string path = Path.Combine(Path.GetTempPath(), $"addon-{Sanitize(source.Name)}-{appId}.{ext}");

        // GithubProxy reports 0..1 fractions; scaled to a byte-shaped report so the queue's UI, which
        // speaks bytes, shows a moving bar rather than nothing.
        var sink = progress is null ? null
            : new ProgressRelay<double?>(f => progress.Report(new DownloadProgress((long)((f ?? 0) * 1000), 1000)));

        Exception? last = null;
        foreach (string url in Urls(source, appId))
        {
            try
            {
                await gh.DownloadAsync(url, path, sink, ct);
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return new DownloadedFile(path, $"{appId}.{ext}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }

        log.LogDebug(last, "Addon source {Source} could not fetch {AppId}", source.Name, appId);
        throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);
    }

    /// <summary>
    /// Does this exact url serve something? HEAD first, and on a host that refuses HEAD, a one-byte
    /// ranged GET.
    /// </summary>
    /// <remarks>
    /// The built-in sources probe with HEAD alone, which is safe because they are known GitHub raw urls.
    /// An addon's url is any host its author chose, and plenty answer 405 or 501 to a HEAD while serving
    /// the file perfectly well over GET — probing with HEAD alone would report those sources as not
    /// having the game, and the user would never see a row for a source that in fact works.
    /// </remarks>
    private async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        head.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
        using var headRes = await _http.SendAsync(head, ct);
        if (headRes.StatusCode == HttpStatusCode.OK) return true;
        if (headRes.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            return false;

        // Range is a request, not a promise: a server may ignore it and start sending the whole file, so
        // the response is disposed without reading the body and only the status is used.
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        get.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
        get.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
        using var getRes = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
        return getRes.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent;
    }

    /// <summary>The primary url then its mirrors, each with the appid substituted.</summary>
    private static IEnumerable<string> Urls(ManifestSourceDescriptor source, long appId)
    {
        yield return Fill(source.UrlTemplate, appId);
        foreach (string m in source.Mirrors) yield return Fill(m, appId);
    }

    private static string Fill(string template, long appId) =>
        template.Replace("{appid}", appId.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps a source name fit for a temp file name; a descriptor's name is user-supplied text.</summary>
    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private async Task<IReadOnlyDictionary<long, string>?> EnsureKeysAsync(
        ManifestSourceDescriptor source, CancellationToken ct)
    {
        // Keyed on the url as well as the name: an addon can be edited and refreshed without restarting
        // the app now, and a cache keyed on the name alone would keep serving the old database after its
        // url was changed - the most confusing possible outcome of an edit that looks like it worked.
        string cacheKey = $"{source.Name}\u0000{source.UrlTemplate}";

        lock (_keyDbs)
            if (_keyDbs.TryGetValue(cacheKey, out var cached)) return cached;

        await _keyGate.WaitAsync(ct);
        try
        {
            lock (_keyDbs)
                if (_keyDbs.TryGetValue(cacheKey, out var cached)) return cached; // won the race

            var keys = await hub.FetchKeyDatabaseAsync(Urls(source, 0), ct);
            if (keys is null) return null;

            lock (_keyDbs) _keyDbs[cacheKey] = keys;
            return keys;
        }
        finally { _keyGate.Release(); }
    }
}
