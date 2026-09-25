using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// The free manifest source that is usually the most current: one zip per appid carrying the lua and
/// its <c>.manifest</c> files. Big titles are refreshed daily; smaller ones lag, so "daily" is a
/// best case, not a guarantee.
/// </summary>
/// <remarks>
/// <para>Since Steam closed the route that served manifests for apps you don't own, a source has to
/// bring its own or the install cannot download. This one does, and it is the freshest that does.</para>
///
/// <para>Listed in lua.tools' own <c>load_free_manifest_apis</c> with <c>"enabled": true</c>, next to
/// Sushi — published as free rather than a private endpoint. It answers without an account.</para>
///
/// <para><b>It rate-limits hard, per IP.</b> A short burst (measured: about eight requests) earns a 429
/// that lasts tens of minutes, and the counter is shared across every request to the host — HEAD, GET,
/// and every appid together. So all traffic here is funnelled through <see cref="Gate"/>: one request at
/// a time, a minimum gap between them, and a cooldown that takes the source out of the running entirely
/// after a 429 rather than hammering it into a longer ban. A 429 is a "wait", never a "no".</para>
///
/// <para><b>Plain HTTP</b>, because no TLS endpoint exists. What that exposes is bounded but real: the
/// installer only ever writes <c>.lua</c> and <c>.manifest</c>, and the lua is forced to
/// <c>&lt;appid&gt;.lua</c>. A manifest's gid is a field stored inside the file, not a hash of its
/// bytes, so a man in the middle could serve a manifest (or a lua) crafted for the game being added.
/// That is why this is a deliberate built-in and not something the addon format allows: addon sources
/// must be https.</para>
/// </remarks>
public class RyuuService(ILogger<RyuuService> log)
{
    /// <summary>The source name this appears under in the Add page's row list.</summary>
    public const string SourceName = "ryuu";

    private static string ZipUrl(long appId) => $"{AppConfig.RyuuBase}/{appId}";

    // One client: the host ignores Range (Accept-Ranges: none) and returns the whole file for any GET,
    // so a "cheap" ranged probe would pull the entire zip. Existence is checked with HEAD, which the
    // host does honour, and HEAD shares the one connection budget with the download.
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromMinutes(5));

    /// <summary>Serializes every call to the host and paces it, because the ban is per-IP and long.</summary>
    private static readonly HostGate Gate = new();

    /// <summary>
    /// Does it have this game? A HEAD that answers 200. A 429 throws <see cref="RateLimitedException"/>
    /// so the caller can keep the row instead of silently dropping the source; any other failure is a
    /// plain "no".
    /// </summary>
    public async Task<bool> HasGameAsync(long appId, CancellationToken ct = default)
    {
        try
        {
            using var res = await Gate.SendAsync(_http, () =>
            {
                var head = new HttpRequestMessage(HttpMethod.Head, ZipUrl(appId));
                head.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
                return head;
            }, HttpCompletionOption.ResponseHeadersRead, ct);

            if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new RateLimitedException();
            return res.StatusCode == HttpStatusCode.OK;
        }
        catch (RateLimitedException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Ryuu probe for {AppId} failed", appId);
            return false;
        }
    }

    /// <summary>Download the game's manifest zip to a temp file, for the install pipeline to unpack.</summary>
    public async Task<DownloadedFile> DownloadZipAsync(
        long appId, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ryuu-{appId}.zip");

        using var res = await Gate.SendAsync(_http, () =>
        {
            var get = new HttpRequestMessage(HttpMethod.Get, ZipUrl(appId));
            get.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
            return get;
        }, HttpCompletionOption.ResponseHeadersRead, ct);

        if (res.StatusCode == HttpStatusCode.TooManyRequests)
            throw new DownloadAbortedException(Resources.Strings.Free_Err_RateLimited);
        if (!res.IsSuccessStatusCode)
            throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        // Streamed with real byte counts rather than a scaled fraction: these zips run to tens of
        // megabytes, and the queue's UI can show size and speed when it is given them.
        long? total = res.Content.Headers.ContentLength;
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                progress?.Report(new DownloadProgress(written, total));
            }
        }

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        return new DownloadedFile(path, $"{appId}.zip");
    }
}

/// <summary>Thrown by a free source when the host answered 429 — a "wait", distinct from "no game".</summary>
public sealed class RateLimitedException : Exception;

/// <summary>
/// A single-flight, paced gate over one host that punishes bursts with a long per-IP ban. Serializes
/// every request, keeps a minimum gap between them, and after a 429 refuses to send for a cooldown so
/// the app steps back instead of extending the ban.
/// </summary>
/// <remarks>
/// Static and shared across every <see cref="RyuuService"/> use, since the ban is on the IP: the four
/// free sources are probed at once, and without this the Ryuu probe would race its own download and the
/// next Fetch's probe against one counter. Not a rolling window like <c>SteamAppInfoCache</c>'s, because
/// there is no measured budget to fill — the host gives no Retry-After and no quota headers — so the
/// safe posture is simply "slowly, one at a time, and back off completely when told no".
/// </remarks>
public sealed class HostGate
{
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private DateTime _lastSentUtc = DateTime.MinValue;
    private DateTime _cooldownUntilUtc = DateTime.MinValue;

    public async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Func<HttpRequestMessage> makeRequest,
        HttpCompletionOption completion, CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (now < _cooldownUntilUtc)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);

            var since = now - _lastSentUtc;
            if (since < MinGap) await Task.Delay(MinGap - since, ct);

            using var req = makeRequest();
            var res = await http.SendAsync(req, completion, ct);
            _lastSentUtc = DateTime.UtcNow;

            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                _cooldownUntilUtc = _lastSentUtc + Cooldown;

            return res;
        }
        finally { _oneAtATime.Release(); }
    }
}
