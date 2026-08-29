using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;

namespace LuaToolsGui.Services;

/// <summary>
/// Caches achievement icons on disk, one folder per game, so a list of 200 achievements downloads its
/// icons once and renders locally/offline afterwards. Same shape as <see cref="CoverCache"/>:
/// concurrent requests for the same icon share a single download, and misses are remembered so a
/// game with broken icon names doesn't retry forever.
/// </summary>
public class AchievementIconCache
{
    private static readonly string IconsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "achievements");

    // Steam serves achievement icons from the community CDN, keyed by app id and the icon file name
    // that the stats schema carries verbatim.
    private static string UrlFor(long appId, string icon) =>
        $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{icon}";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // A game can have 300 achievements, and "unlock all" flips every icon to its other variant at once.
    // Without a gate that's 300 simultaneous CDN requests; eight at a time fills the list just as fast
    // without looking like a burst to Steam.
    private readonly SemaphoreSlim _downloadGate = new(8, 8);
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    private readonly ConcurrentDictionary<string, byte> _missing = new();

    /// <summary>
    /// Local path for a cached icon, downloading it first if needed. Returns null when the icon name is
    /// empty or the CDN has nothing for it (both are normal: not every game ships gray icons).
    /// </summary>
    public Task<string?> EnsureAsync(long appId, string icon, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(icon)) return Task.FromResult<string?>(null);

        // The icon name comes from a game-authored schema file, so it never gets to touch the path:
        // anything but a plain file name is refused rather than sanitized.
        if (icon.AsSpan().IndexOfAny('/', '\\', ':') >= 0 || icon.Contains("..", StringComparison.Ordinal))
            return Task.FromResult<string?>(null);

        string key = $"{appId}/{icon}";
        if (_missing.ContainsKey(key)) return Task.FromResult<string?>(null);

        string path = Path.Combine(IconsDir, appId.ToString(), icon);
        if (File.Exists(path)) return Task.FromResult<string?>(path);

        return _inFlight.GetOrAdd(key, _ => DownloadAsync(appId, icon, key, path, ct));
    }

    private async Task<string?> DownloadAsync(long appId, string icon, string key, string path, CancellationToken ct)
    {
        bool acquired = false;
        try
        {
            await _downloadGate.WaitAsync(ct);
            acquired = true;

            byte[] bytes = await _http.GetByteArrayAsync(UrlFor(appId, icon), ct);
            if (bytes.Length == 0)
            {
                _missing[key] = 0;
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write then move: a half-written file must never be picked up as a valid cached icon.
            string temp = path + ".part";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException)
        {
            return null; // page closed mid-download. Not a miss: let it retry next time
        }
        catch
        {
            _missing[key] = 0; // 404 / offline. Stop asking for this one this session
            return null;
        }
        finally
        {
            if (acquired) _downloadGate.Release();
            _inFlight.TryRemove(key, out _);
        }
    }
}
