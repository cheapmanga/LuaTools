using System.IO;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>
/// Reads how long the signed-in account has played a game, from Steam's own
/// <c>userdata\&lt;account&gt;\config\localconfig.vdf</c>.
///
/// <para>
/// It exists for one warning: Steam rolls back achievements that don't square with the recorded
/// playtime, so unlocking a full list on a game with five minutes on the clock is the one way to lose
/// the lot. Knowing the playtime is what lets the app say so before the user commits.
/// </para>
///
/// <para>
/// Best-effort and fails closed: an unreadable file, a Steam that never wrote one, or a game that was
/// never launched all return null, which callers must read as "don't warn" rather than "zero minutes".
/// Warning on a missing number would train the user to click through it.
/// </para>
/// </summary>
public partial class SteamPlaytimeService(SteamService steam)
{
    /// <summary>The 64-bit ids Steam hands out start here; the folder under userdata\ is the remainder.</summary>
    private const ulong SteamIdBase = 76561197960265728;

    public static long AccountIdFrom(ulong steamId) => (long)(steamId - SteamIdBase);

    /// <summary>
    /// Minutes played, or null when it can't be established. Steam stores this per account, so the
    /// caller passes the id of the account actually signed in rather than us guessing among the
    /// folders under userdata\.
    /// </summary>
    public int? GetMinutesPlayed(long accountId, long appId)
    {
        string? steamRoot = steam.EffectivePath;
        if (steamRoot is null || accountId <= 0) return null;

        string path = Path.Combine(steamRoot, "userdata", accountId.ToString(), "config", "localconfig.vdf");
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
        }
        catch { return null; } // locked by a running Steam, or unreadable

        return ReadPlaytime(text, appId);
    }

    /// <summary>
    /// Pull one game's Playtime out of a localconfig.vdf.
    ///
    /// <para>
    /// The file is Valve's text KeyValues. Rather than parse the whole tree, this finds the block named
    /// after the app id and reads its Playtime — but only accepts a block that actually has one, because
    /// an app id appears under several unrelated sections of this file and only one of them is the
    /// per-game record.
    /// </para>
    /// </summary>
    internal static int? ReadPlaytime(string localConfig, long appId)
    {
        foreach (Match match in AppBlockRegex(appId).Matches(localConfig))
        {
            string? block = ReadBalancedBlock(localConfig, match.Index + match.Length - 1);
            if (block is null) continue;

            var playtime = PlaytimeRegex().Match(block);
            if (playtime.Success && int.TryParse(playtime.Groups[1].Value, out int minutes)) return minutes;
        }

        return null;
    }

    /// <summary>Take the { … } starting at <paramref name="open"/>, counting braces so nested blocks
    /// don't end it early. Null if the file is truncated.</summary>
    private static string? ReadBalancedBlock(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[open..(i + 1)];
        }
        return null;
    }

    // "440"\n{ … } — the appid as a quoted key, then its block.
    private static Regex AppBlockRegex(long appId) =>
        new("\"" + appId + "\"\\s*\\{", RegexOptions.None, TimeSpan.FromSeconds(2));

    [GeneratedRegex("\"Playtime\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PlaytimeRegex();
}
