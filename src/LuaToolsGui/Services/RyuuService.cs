using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// The free manifest source that is actually current: one zip per appid carrying the lua and its
/// <c>.manifest</c> files, refreshed daily.
/// </summary>
/// <remarks>
/// <para>Since Steam closed the route that served manifests for apps you don't own, a source has to
/// bring its own or the install cannot download. That leaves this one: <see cref="SushiService"/>'s repo
/// stopped in November 2025, and every ManifestHub fork is a mid-2025 snapshot of the same data.
/// <see cref="ManifestHubService"/> ships depot keys and no manifests at all, so it is degraded until
/// the route reopens.</para>
///
/// <para>Listed in lua.tools' own <c>load_free_manifest_apis</c> with <c>"enabled": true</c>, next to
/// Sushi — published as free rather than a private endpoint. It answers without an account.</para>
///
/// <para><b>Plain HTTP</b>, because no TLS endpoint exists. What that exposes is bounded: the installer
/// only ever writes <c>.lua</c> and <c>.manifest</c>, manifest filenames are content-addressed so a
/// tampered one simply won't match what Steam asks for, and the lua is forced to
/// <c>&lt;appid&gt;.lua</c>. A man in the middle could still hand over a bad lua for the game being
/// added. That is why this lives in the app as a deliberate choice rather than as a source anyone can
/// declare: the addon format refuses anything that isn't https.</para>
/// </remarks>
public class RyuuService(ILogger<RyuuService> log)
{
    /// <summary>The source name this appears under in the Add page's row list.</summary>
    public const string SourceName = "ryuu";

    private static string ZipUrl(long appId) => $"{AppConfig.RyuuBase}/{appId}";

    // Not GithubProxy: this is not GitHub, and its mirrors would only add wasted hops. Two clients so a
    // cheap existence probe never inherits the long timeout a multi-megabyte download needs.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Does it have this game? Any failure answers "no", never an error.</summary>
    public async Task<bool> HasGameAsync(long appId, CancellationToken ct = default)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, ZipUrl(appId));
            head.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
            using var res = await _probe.SendAsync(head, ct);
            if (res.StatusCode == HttpStatusCode.OK) return true;

            // A server that refuses HEAD would otherwise read as "doesn't have this game" for every
            // title, silently dropping the only current free source out of the list.
            if (res.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
                return false;

            using var get = new HttpRequestMessage(HttpMethod.Get, ZipUrl(appId));
            get.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
            get.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            using var ranged = await _probe.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            return ranged.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent;
        }
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

        using var res = await _http.GetAsync(ZipUrl(appId), HttpCompletionOption.ResponseHeadersRead, ct);
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
