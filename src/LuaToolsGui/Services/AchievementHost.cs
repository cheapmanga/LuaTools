using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>One achievement of a game, as reported by the host (schema + this account's state).</summary>
public record SteamAchievement(
    string Id,
    string Name,
    string Description,
    bool IsHidden,
    bool IsProtected,
    bool IsAchieved,
    long UnlockTime,
    string Icon,
    string IconLocked)
{
    /// <summary>Unlock date in local time, or null when still locked (or unlocked without a timestamp).</summary>
    public DateTime? UnlockedAt => IsAchieved && UnlockTime > 0
        ? DateTimeOffset.FromUnixTimeSeconds(UnlockTime).LocalDateTime
        : null;
}

/// <summary>
/// A failure reported by the achievement host. <see cref="Code"/> is a stable identifier
/// (<c>steam_not_running</c>, <c>no_schema</c>, …) meant to be mapped to a localized message;
/// <see cref="Exception.Message"/> is the host's English fallback text.
/// </summary>
public class AchievementHostException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// A live achievement host process, bound to one game for its whole life.
///
/// <para>
/// The host is a separate x86 process because steamclient.dll is 32-bit and can't be loaded into
/// LuaTools (64-bit), and because the Steam pipe bakes in the app id at load time — so "one process
/// per opened game" isn't a workaround, it's the only shape that works. See
/// <c>src/LuaTools.SamHost</c> for the protocol.
/// </para>
///
/// <para>
/// Commands are serialized: the protocol is one request, one response, in order. Disposing sends
/// <c>quit</c> and then kills the process if it doesn't leave on its own.
/// </para>
/// </summary>
public sealed class AchievementSession : IDisposable
{
    // list() waits on Steam (the host allows itself 15s for the stats callback), so the read timeout
    // has to sit above that. Everything else answers immediately.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private AchievementSession(Process process, long appId, string language)
    {
        _process = process;
        AppId = appId;
        Language = language;
    }

    public long AppId { get; }

    /// <summary>Steam's language for this game; the achievement names come back in it.</summary>
    public string Language { get; }

    /// <summary>
    /// Start a host for <paramref name="appId"/> and wait for its ready line.
    /// </summary>
    /// <exception cref="AchievementHostException">
    /// Steam isn't running/installed, the host is missing from the install, or it refused the app id.
    /// </exception>
    public static async Task<AchievementSession> OpenAsync(
        string hostPath, long appId, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(hostPath, appId.ToString())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new AchievementHostException("host_start_failed", "Could not start the achievement host.");
        }
        catch (Exception ex) when (ex is not AchievementHostException)
        {
            throw new AchievementHostException("host_start_failed", ex.Message);
        }

        // Drain stderr in the background. The host isn't supposed to write any, but an unhandled
        // exception would, and a full 4 KB pipe with nobody reading it deadlocks the child mid-write.
        _ = process.StandardError.ReadToEndAsync();

        try
        {
            using var ready = await ReadReplyAsync(process, ct);
            string language = ready.RootElement.TryGetProperty("language", out var lang)
                ? lang.GetString() ?? "english"
                : "english";
            return new AchievementSession(process, appId, language);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }
    }

    /// <summary>Ask Steam for this account's stats and return every achievement with its state.</summary>
    public async Task<IReadOnlyList<SteamAchievement>> ListAsync(CancellationToken ct = default)
    {
        using var reply = await SendAsync("list", ct);
        var list = new List<SteamAchievement>();

        if (!reply.RootElement.TryGetProperty("achievements", out var items)) return list;

        foreach (var item in items.EnumerateArray())
        {
            list.Add(new SteamAchievement(
                Id: item.GetProperty("id").GetString() ?? "",
                Name: item.GetProperty("name").GetString() ?? "",
                Description: item.GetProperty("desc").GetString() ?? "",
                IsHidden: item.GetProperty("hidden").GetBoolean(),
                IsProtected: item.GetProperty("protected").GetBoolean(),
                IsAchieved: item.GetProperty("achieved").GetBoolean(),
                UnlockTime: item.GetProperty("unlockTime").GetInt64(),
                Icon: item.GetProperty("icon").GetString() ?? "",
                IconLocked: item.GetProperty("iconLocked").GetString() ?? ""));
        }

        return list;
    }

    /// <summary>Stage one achievement. Nothing reaches Steam until <see cref="StoreAsync"/>.</summary>
    public async Task SetAsync(string id, bool achieved, CancellationToken ct = default)
    {
        using var _ = await SendAsync($"set {(achieved ? 1 : 0)} {id}", ct);
    }

    /// <summary>Stage every non-protected achievement. Returns how many were staged.</summary>
    public async Task<int> SetAllAsync(bool achieved, CancellationToken ct = default)
    {
        using var reply = await SendAsync($"setall {(achieved ? 1 : 0)}", ct);
        return reply.RootElement.TryGetProperty("staged", out var staged) ? staged.GetInt32() : 0;
    }

    /// <summary>Commit every staged change to Steam. The point of no return.</summary>
    public async Task StoreAsync(CancellationToken ct = default)
    {
        using var _ = await SendAsync("store", ct);
    }

    /// <summary>
    /// Reset this game's progress. Steam's only reset always clears the numeric stats;
    /// <paramref name="achievementsToo"/> extends it to achievements. Immediate, no store needed.
    /// </summary>
    public async Task ResetAsync(bool achievementsToo, CancellationToken ct = default)
    {
        using var _ = await SendAsync($"reset {(achievementsToo ? 1 : 0)}", ct);
    }

    private async Task<JsonDocument> SendAsync(string command, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct);
        try
        {
            if (_process.HasExited)
                throw new AchievementHostException("host_gone", "The achievement host stopped unexpectedly.");

            await _process.StandardInput.WriteAsync(command.AsMemory(), ct);
            await _process.StandardInput.WriteAsync("\n".AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
            return await ReadReplyAsync(_process, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Read one protocol line and turn a failure reply into an exception. A null line means the host
    /// died (a Steam-side crash inside the interop, most likely), which we report rather than hang on.
    /// </summary>
    private static async Task<JsonDocument> ReadReplyAsync(Process process, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);

        string? line;
        try
        {
            line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AchievementHostException("host_timeout", "The achievement host stopped responding.");
        }

        if (line is null)
            throw new AchievementHostException("host_gone", "The achievement host stopped unexpectedly.");

        JsonDocument reply;
        try
        {
            reply = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            throw new AchievementHostException("bad_reply", "Unreadable answer from the achievement host.");
        }

        if (reply.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return reply;

        string code = reply.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "unknown" : "unknown";
        string error = reply.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        reply.Dispose();
        throw new AchievementHostException(code, error);
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        process.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Ask nicely first: a clean exit releases the Steam pipe instead of leaving it to the OS.
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("quit");
                _process.StandardInput.Flush();
                _process.WaitForExit(1500);
            }
        }
        catch { /* the pipe may already be closed. Fall through to the kill */ }

        KillQuietly(_process);
        _gate.Dispose();
    }
}

/// <summary>
/// Locates the achievement host and exposes the two things LuaTools needs from it: a per-game session,
/// and a one-shot scan of Steam's cached achievement schemas (used to label the game grid without
/// opening a Steam connection per game).
/// </summary>
public class AchievementHostService
{
    private const string HostFileName = "LuaTools.SamHost.exe";

    /// <summary>Full path to the host, which ships next to LuaTools.exe.</summary>
    public static string HostPath => Path.Combine(AppContext.BaseDirectory, HostFileName);

    /// <summary>False when the host is missing from the install (a broken/partial update).</summary>
    public static bool IsAvailable => File.Exists(HostPath);

    /// <summary>Open a session for one game. The caller owns it and must dispose it.</summary>
    public Task<AchievementSession> OpenAsync(long appId, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new AchievementHostException("host_missing", $"{HostFileName} is missing from the LuaTools folder.");

        return AchievementSession.OpenAsync(HostPath, appId, ct);
    }

    /// <summary>
    /// appid → achievement count for every schema Steam has cached on this machine. Steam writes those
    /// files when it fetches a game's stats, so this covers what the account has actually played, and
    /// costs one short-lived process for the whole library.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, int>> ScanSchemasAsync(CancellationToken ct = default)
    {
        var counts = new Dictionary<long, int>();
        if (!IsAvailable) return counts;

        var startInfo = new ProcessStartInfo(HostPath, "--schemas")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return counts;

            // Both pipes are read before waiting: reading only one can deadlock on the other filling up.
            var errors = process.StandardError.ReadToEndAsync(ct);
            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await errors;
            await process.WaitForExitAsync(ct);

            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("schemas", out var schemas)) return counts;

            foreach (var entry in schemas.EnumerateArray())
                counts[entry.GetProperty("appid").GetInt64()] = entry.GetProperty("count").GetInt32();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // No Steam, no cache folder, unreadable output: the grid just shows no counts.
        }

        return counts;
    }
}
