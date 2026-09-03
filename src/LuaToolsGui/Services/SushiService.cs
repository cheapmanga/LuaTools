using System.IO;
using System.Net;
using System.Net.Http;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// A second free, account-free manifest source: SteamTools' public game repo (sushi-dev55-alt), one
/// <c>&lt;appid&gt;.zip</c> per game.
/// </summary>
/// <remarks>
/// <para>Where <see cref="ManifestHubService"/> holds only depot keys and synthesises a lua, this repo
/// ships a full manifest zip - the lua AND the <c>.manifest</c> files - so it can cover games and pinned
/// builds a keys-only source can't. No account, no daily cap; just a public GitHub raw file.</para>
///
/// <para>Existence is a cheap HEAD against the raw URL (a game's zip is tiny); the download itself goes
/// through <see cref="GithubProxy"/> for the same mirror fallback as everything else, and the existing
/// install pipeline unpacks the zip exactly as it would a lua.tools one.</para>
/// </remarks>
public class SushiService(GithubProxy gh, ILogger<SushiService> log)
{
    /// <summary>The source name this appears under in the Add page's row list.</summary>
    public const string SourceName = "sushi";

    private static string ZipUrl(long appId) => $"{AppConfig.SushiRawBase}/{appId}.zip";

    // HEAD is a bare existence probe; the real download uses GithubProxy. Its own client so the probe
    // never inherits a long download timeout.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Does the repo have a zip for this game? A HEAD that answers 200; 404 (or any failure) means no.
    /// </summary>
    public async Task<bool> HasGameAsync(long appId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, ZipUrl(appId));
            req.Headers.TryAddWithoutValidation("User-Agent", "LuaTools");
            using var res = await _http.SendAsync(req, ct);
            return res.StatusCode == HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Sushi HEAD for {AppId} failed", appId);
            return false;
        }
    }

    /// <summary>
    /// Download the game's manifest zip to a temp file, for the install pipeline to unpack.
    /// </summary>
    public async Task<DownloadedFile> DownloadZipAsync(
        long appId, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sushi-{appId}.zip");

        // GithubProxy reports 0..1 fractions; the zips are small, so an indeterminate bar is fine.
        var sink = progress is null ? null
            : new ProgressRelay<double?>(f => progress.Report(new DownloadProgress((long)((f ?? 0) * 1000), 1000)));

        await gh.DownloadAsync(ZipUrl(appId), path, sink, ct);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new DownloadAbortedException(Resources.Strings.Free_Err_Unavailable);

        return new DownloadedFile(path, $"{appId}.zip");
    }
}
