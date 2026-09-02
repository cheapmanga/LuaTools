using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// A third-party tool published as a single .exe in a GitHub release: kept in our own folder, updated
/// from that release, and opened on demand.
/// </summary>
/// <remarks>
/// <para>The shape is <see cref="SteamAutoCrackService"/>'s, minus the parts that only its ZIP needs:
/// the release is looked up through <see cref="GithubProxy"/> (direct, then mirrors), the asset digest
/// is verified before it replaces a working copy, the installed tag is recorded, and the check is
/// throttled so a click on an offline machine backs off instead of re-walking the mirror chain.</para>
///
/// <para>Every failure path falls back to an existing exe while still recording the attempt: a tool
/// that is already on disk stays launchable when GitHub is unreachable.</para>
///
/// <para>Like SteamAutoCrack, these can only be OPENED. Nothing about what the user does inside them is
/// driven from here.</para>
/// </remarks>
public abstract class GithubExeTool(GithubProxy gh, CacheService cache, ILogger log)
{
    /// <summary>owner/repo the release is read from.</summary>
    protected abstract string Repo { get; }

    /// <summary>The release asset to take, matched by exact file name.</summary>
    protected abstract string AssetName { get; }

    /// <summary>Folder under %AppData%\LuaToolsGui, and the key this tool's version is cached under.</summary>
    public abstract string Id { get; }

    /// <summary>The tool's own name. A product name: shown as-is, never translated.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Whether this tool is a framework-dependent .NET app that needs a Desktop runtime.</summary>
    public virtual bool NeedsDotnetDesktop => false;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long an up-to-date check is trusted before we ask GitHub again.</summary>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", Id);

    public string ExePath => Path.Combine(Dir, AssetName);

    /// <summary>The queue's dedupe key, so repeated clicks join the running item instead of stacking.</summary>
    public string JobKey => $"tool:{Id}";

    private bool CheckedRecently() =>
        cache.GetToolCheckedAt(Id) is > 0 and var last
        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last < (long)ToolCheckInterval.TotalMilliseconds;

    private void RecordAttempt() =>
        cache.SaveToolCheck(Id, cache.GetToolVersion(Id), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// Ensure the tool is on disk and reasonably current. Null only if no usable copy exists.
    /// </summary>
    /// <param name="force">
    /// Skip the throttle - load-bearing for the background-update path, whose probe already recorded the
    /// check timestamp and would otherwise make this skip the very download it was queued to perform.
    /// </param>
    public async Task<string?> EnsureToolAsync(
        IProgress<DownloadProgress>? progress, bool force = false, CancellationToken ct = default)
    {
        if (!force && File.Exists(ExePath) && CheckedRecently()) return ExePath;

        await _gate.WaitAsync(ct);
        bool have = false;
        try
        {
            have = File.Exists(ExePath);
            if (!force && have && CheckedRecently()) return ExePath; // won the race

            string url = $"https://api.github.com/repos/{Repo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                log.LogDebug("{Tool} release lookup failed: {Status}", DisplayName, res?.StatusCode);
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(
                a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                log.LogDebug("{Tool} release has no {Asset}", DisplayName, AssetName);
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            if (!force && have && !string.IsNullOrEmpty(release!.TagName)
                             && string.Equals(release.TagName, cache.GetToolVersion(Id), StringComparison.Ordinal))
            {
                RecordAttempt();
                return ExePath;
            }

            Directory.CreateDirectory(Dir);

            // Downloaded beside the real exe, then moved into place: we launch this binary, so a
            // half-written or tampered file must never replace a copy that works.
            string staged = ExePath + ".new";
            await gh.DownloadAsync(asset.DownloadUrl, staged, Sink(progress, asset.Size), ct);

            if (!AssetHash.Matches(staged, asset.Digest))
            {
                log.LogDebug("{Tool} asset digest mismatch; keeping the existing copy", DisplayName);
                try { File.Delete(staged); } catch { }
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            // Throws if the user has the tool OPEN (the exe is locked), which a background update can
            // easily hit. Caught below: it falls back to the existing copy and retries next interval.
            File.Move(staged, ExePath, overwrite: true);

            cache.SaveToolCheck(Id, release!.TagName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return ExePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Obtaining {Tool} failed", DisplayName);
            if (have) RecordAttempt();
            return have ? ExePath : null;
        }
        finally { _gate.Release(); }
    }

    private static ProgressRelay<double?>? Sink(IProgress<DownloadProgress>? progress, long size) =>
        progress is null ? null : new ProgressRelay<double?>(f =>
            progress.Report(new DownloadProgress((long)((f ?? 0) * size), size > 0 ? size : null)));

    /// <summary>
    /// Throttled "is there a newer build?" probe. No download, and never blocks a launch.
    /// </summary>
    public async Task<bool> IsUpdateAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath)) return false;
        if (CheckedRecently()) return false;

        await _gate.WaitAsync(ct);
        try
        {
            if (CheckedRecently()) return false; // won the race

            string url = $"https://api.github.com/repos/{Repo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) { RecordAttempt(); return false; }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            RecordAttempt();

            return !string.IsNullOrEmpty(release?.TagName)
                && !string.Equals(release!.TagName, cache.GetToolVersion(Id), StringComparison.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Checking for a {Tool} update failed", DisplayName);
            RecordAttempt();
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hook for a tool that needs its process prepared (a private runtime, say).</summary>
    protected virtual void Configure(ProcessStartInfo psi) { }

    /// <summary>Open the tool's window. Fire-and-forget; we don't wait for it to exit.</summary>
    public bool Launch()
    {
        if (!File.Exists(ExePath)) return false;
        try
        {
            // Shell by default, exactly as before; Configure turns it off only when it has environment
            // variables to pass (a private runtime). Both tools are asInvoker, so neither path elevates.
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Dir,
            };
            Configure(psi);

            try
            {
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740 && !psi.UseShellExecute)
            {
                // ERROR_ELEVATION_REQUIRED: only the shell can raise a UAC prompt, so a tool that starts
                // asking for admin cannot be given the private runtime. Start it the old way and let it
                // find a runtime itself - a Windows dialog beats a launch that just fails.
                log.LogDebug("{Tool} wants elevation; launching without the private runtime", DisplayName);
                Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Dir });
            }

            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Launching {Tool} failed", DisplayName);
            return false;
        }
    }
}

/// <summary>
/// Tokeer (Tesla697/TokeerDRM-App): shares and redeems Denuvo activation codes.
/// </summary>
/// <remarks>Their exe is a Nuitka onefile build of a Python app - self-contained, nothing to install
/// alongside it.</remarks>
public sealed class TokeerAppService(GithubProxy gh, CacheService cache, ILogger<TokeerAppService> log)
    : GithubExeTool(gh, cache, log)
{
    protected override string Repo => AppConfig.TokeerRepo;
    protected override string AssetName => "TokeerDRM.exe";
    public override string Id => "tokeer";
    public override string DisplayName => "Tokeer";
}

/// <summary>
/// LuaToolsValidator (Tesla697/LuaToolsValidator): a front end for the Denuvo activation validator.
/// </summary>
/// <remarks>
/// A framework-dependent net10.0-windows build (its runtimeconfig asks for Microsoft.WindowsDesktop.App
/// 10.0.0), so it cannot start on a machine without that runtime. Rather than making the user install
/// one, it is launched against <see cref="PrivateDotnetRuntime"/>.
/// </remarks>
public sealed class LuaToolsValidatorService(
    GithubProxy gh, CacheService cache, PrivateDotnetRuntime runtime, ILogger<LuaToolsValidatorService> log)
    : GithubExeTool(gh, cache, log)
{
    protected override string Repo => AppConfig.LuaToolsValidatorRepo;
    protected override string AssetName => "LuaToolsValidator.exe";
    public override string Id => "luatoolsvalidator";
    public override string DisplayName => "LuaTools Validator";
    public override bool NeedsDotnetDesktop => true;

    protected override void Configure(ProcessStartInfo psi) => runtime.Apply(psi);
}
