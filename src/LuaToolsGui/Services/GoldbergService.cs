using System.IO;
using System.IO.Compression;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Outcome of applying the Steam emulator over a game's steam_api DLLs.</summary>
public record GoldbergResult(int Applied, int AlreadyDone, int Total, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Applies the Goldberg Steam emulator (gbe_fork) to a game: each <c>steam_api.dll</c> /
/// <c>steam_api64.dll</c> is backed up and replaced by the emulator's, with a
/// <c>steam_settings\steam_appid.txt</c> beside it so the emulator knows which game it is answering for.
///
/// <para>
/// This is the second half of what "Remove Steam DRM" does: Steamless strips the SteamStub wrapper off
/// the executable, and this replaces the Steam API the game then calls. Stripping alone leaves a game
/// that still wants a running, owning Steam client.
/// </para>
///
/// <para>
/// The behaviour deliberately mirrors <see href="https://github.com/SteamAutoCracks/Steam-auto-crack">
/// SteamAutoCrack</see>'s defaults, which is the tool that automates this pipeline: the *regular*
/// emulator build rather than the experimental one, a <c>.dll.bak</c> backup, and a game that already
/// has one is left alone rather than patched twice. Its CLI is never published in a release (only the
/// GUI is), so the steps are done here instead of shelling out to it.
/// </para>
///
/// <para>
/// The emulator binaries come from SteamAutoCrack's own release package rather than from gbe_fork
/// directly, and it makes no difference to what lands in the game folder: the DLLs in the two are
/// byte-identical (same SHA-256, checked 2026-08-29). gbe_fork publishes its Windows builds as
/// <c>.7z</c> only, which .NET cannot open without pulling in a decoder, while this package is a plain
/// zip laid out as regular|experimental × x86|x64. Same emulator, one dependency fewer.
/// </para>
/// </summary>
public class GoldbergService(GithubProxy gh, SteamLibraryService library)
{
    private static readonly string EmuDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "goldberg");

    // Inside the package, and inside EmuDir once extracted. "regular" is SteamAutoCrack's default;
    // "experimental" ships too and is kept, so switching later is a path change, not a re-download.
    private const string Variant = "regular";
    private static string EmuDllPath(bool x64) =>
        Path.Combine(EmuDir, Variant, x64 ? "x64" : "x86", x64 ? "steam_api64.dll" : "steam_api.dll");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _emuGate = new(1, 1);

    /// <summary>Ensure the emulator is on disk (downloads + extracts once). Null if it can't be obtained.</summary>
    public async Task<string?> EnsureEmuAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (File.Exists(EmuDllPath(x64: true))) return EmuDir;

        await _emuGate.WaitAsync(ct);
        try
        {
            if (File.Exists(EmuDllPath(x64: true))) return EmuDir; // won the race elsewhere

            string url = $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            Directory.CreateDirectory(EmuDir);
            string zipPath = Path.Combine(EmuDir, "package.zip");
            await gh.DownloadAsync(asset.DownloadUrl, zipPath, progress, ct);

            // Only the emulator: the package also carries a 27 MB GUI executable we have no use for.
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.Length == 0 || entry.Name.Length == 0) continue; // directory entry
                    if (!entry.FullName.StartsWith("Goldberg/", StringComparison.OrdinalIgnoreCase)) continue;

                    string relative = entry.FullName["Goldberg/".Length..];
                    string dest = Path.GetFullPath(Path.Combine(EmuDir, relative));
                    // A zip can name entries "../…"; refuse anything that would land outside EmuDir.
                    if (!dest.StartsWith(EmuDir, StringComparison.OrdinalIgnoreCase)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            try { File.Delete(zipPath); } catch { /* leftover zip is harmless */ }
            return File.Exists(EmuDllPath(x64: true)) ? EmuDir : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _emuGate.Release(); }
    }

    /// <summary>
    /// Swap every steam_api DLL in the game folder for the emulator's. Best-effort per file: one
    /// unwritable DLL (game running, permissions) doesn't stop the others.
    /// </summary>
    public async Task<GoldbergResult> ApplyAsync(long appId, IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? installDir = library.GetInstallDir(appId);
        if (installDir is null) return new GoldbergResult(0, 0, 0, "no-install");

        if (await EnsureEmuAsync(progress, ct) is null) return new GoldbergResult(0, 0, 0, "emu");

        List<string> dlls;
        try
        {
            dlls = Directory.EnumerateFiles(installDir, "steam_api*.dll", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(p).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new GoldbergResult(0, 0, 0, "no-dll"); }

        if (dlls.Count == 0) return new GoldbergResult(0, 0, 0, "no-dll");

        int applied = 0, already = 0;
        foreach (string dll in dlls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string backup = dll + ".bak";
                if (File.Exists(backup))
                {
                    // Already emulated: replacing again would back up the emulator over the real DLL
                    // and lose the original for good.
                    already++;
                    continue;
                }

                bool x64 = Path.GetFileName(dll).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase);
                File.Move(dll, backup);
                File.Copy(EmuDllPath(x64), dll, overwrite: true);
                WriteAppIdFile(Path.GetDirectoryName(dll)!, appId);
                applied++;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Locked or read-only. Put the original back if the move went through but the copy didn't.
                try
                {
                    if (!File.Exists(dll) && File.Exists(dll + ".bak")) File.Move(dll + ".bak", dll);
                }
                catch { /* nothing else to try */ }
            }
        }

        return new GoldbergResult(applied, already, dlls.Count, null);
    }

    /// <summary>
    /// The emulator reads the app id from <c>steam_settings\steam_appid.txt</c>. An existing
    /// steam_settings folder is left alone: it may hold a hand-tuned configuration (DLC list, achievements,
    /// account name) that is worth more than our one line.
    /// </summary>
    private static void WriteAppIdFile(string dllDir, long appId)
    {
        string settings = Path.Combine(dllDir, "steam_settings");
        if (Directory.Exists(settings)) return;

        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "steam_appid.txt"), appId.ToString());
    }
}
