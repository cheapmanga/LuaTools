namespace LuaToolsGui.Services;

/// <summary>
/// Answers one question for the Add page: does this game have Denuvo fixes, and how many?
///
/// <para>
/// The listing endpoint returns every game that has a fix in one shot, so it is fetched once per
/// session and kept as an appid → count map. That keeps "did the game I just fetched have a fix?"
/// free after the first lookup, instead of a per-game request while the user types.
/// </para>
///
/// <para>
/// Best-effort by design: no listing (offline, API down) means no banner, never an error. A failed
/// load isn't cached, so the next lookup tries again.
/// </para>
/// </summary>
public class FixLookupService(LuaToolsApiClient api)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<long, int>? _counts;

    /// <summary>How many fixes exist for this appid. 0 when there are none, or when the listing
    /// couldn't be loaded (the caller can't tell the difference, and doesn't need to).</summary>
    public async Task<int> GetFixCountAsync(long appId, CancellationToken ct = default)
    {
        var counts = await EnsureLoadedAsync(ct);
        return counts is not null && counts.TryGetValue(appId, out int count) ? count : 0;
    }

    private async Task<IReadOnlyDictionary<long, int>?> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_counts is not null) return _counts;

        await _gate.WaitAsync(ct);
        try
        {
            if (_counts is not null) return _counts; // won the race elsewhere

            var listing = await api.GetDenuvoListingsAsync(ct);
            if (listing is null) return null; // don't cache a failure. Retry on the next lookup

            var counts = new Dictionary<long, int>();
            foreach (var game in listing.Games)
                if (long.TryParse(game.AppId, out long id) && game.FixCount > 0)
                    counts[id] = game.FixCount;

            return _counts = counts;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null; // same as above: silence, and try again next time
        }
        finally
        {
            _gate.Release();
        }
    }
}
