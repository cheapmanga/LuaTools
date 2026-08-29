using System.IO;
using System.Text;
using LuaTools.SamHost;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for <see cref="SchemaReader"/>, which turns Steam's binary
/// <c>appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin</c> into the achievement list the
/// Achievements page shows. It parses a file LuaTools doesn't control, so the failure modes worth
/// pinning down are the format variants (localized strings, old vs new stat typing) and the flags that
/// decide whether a row is editable at all.
///
/// <para>
/// The fixtures are written here rather than checked in: a real schema is a third-party binary, and
/// generating one keeps the format we expect visible in the test itself.
/// </para>
/// </summary>
public class AchievementSchemaTests : IDisposable
{
    private const long AppId = 480;

    private readonly string _steam = Path.Combine(Path.GetTempPath(), $"achtest_{Guid.NewGuid():N}");

    public AchievementSchemaTests() =>
        Directory.CreateDirectory(Path.Combine(_steam, "appcache", "stats"));

    [Fact]
    public void Read_ParsesAchievements_AndIgnoresNumericStats()
    {
        WriteSchema(AppId);

        var achievements = SchemaReader.Read(_steam, AppId, "english");

        Assert.NotNull(achievements);
        // The integer stat in the fixture must not show up as an achievement.
        Assert.Equal(2, achievements!.Count);

        var first = achievements[0];
        Assert.Equal("ACH_WIN_ONE_GAME", first.Id);
        Assert.Equal("Winner", first.Name);
        Assert.Equal("Win a game", first.Description);
        Assert.Equal("win.jpg", first.IconNormal);
        Assert.Equal("win_gray.jpg", first.IconLocked);
        Assert.False(first.IsHidden);
        Assert.False(first.IsProtected);
    }

    [Fact]
    public void Read_PrefersRequestedLanguage_ThenFallsBackToEnglish()
    {
        WriteSchema(AppId);

        var french = SchemaReader.Read(_steam, AppId, "french");
        var german = SchemaReader.Read(_steam, AppId, "german"); // absent from the fixture

        Assert.Equal("Gagnant", french![0].Name);
        Assert.Equal("Winner", german![0].Name);
    }

    [Fact]
    public void Read_FlagsHiddenAndServerAwardedAchievements()
    {
        WriteSchema(AppId);

        var protectedAchievement = SchemaReader.Read(_steam, AppId, "english")![1];

        Assert.True(protectedAchievement.IsHidden);
        // permission 2 = awarded by the game's servers: the UI must show it read-only.
        Assert.True(protectedAchievement.IsProtected);
    }

    [Fact]
    public void Read_ReturnsNull_WhenSteamHasNoSchemaCached()
    {
        Assert.Null(SchemaReader.Read(_steam, 999999, "english"));
    }

    [Fact]
    public void ScanCounts_ReportsEveryCachedSchema()
    {
        WriteSchema(AppId);
        WriteSchema(570);

        var counts = new Dictionary<long, int>(SchemaReader.ScanCounts(_steam));

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[AppId]);
        Assert.Equal(2, counts[570]);
    }

    /// <summary>
    /// Write a minimal but realistic schema: one achievements block in the modern string-typed format
    /// (two achievements, one of them hidden and server-awarded) plus an old-style integer stat that
    /// the reader has to skip.
    /// </summary>
    private void WriteSchema(long appId)
    {
        string path = Path.Combine(_steam, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        Node(writer, appId.ToString());
        Node(writer, "stats");

        Node(writer, "1");
        String(writer, "type", "achievements");
        Node(writer, "bits");

        Node(writer, "0");
        String(writer, "name", "ACH_WIN_ONE_GAME");
        Node(writer, "display");
        Node(writer, "name");
        String(writer, "english", "Winner");
        String(writer, "french", "Gagnant");
        End(writer);
        Node(writer, "desc");
        String(writer, "english", "Win a game");
        End(writer);
        String(writer, "hidden", "0");
        String(writer, "icon", "win.jpg");
        String(writer, "icon_gray", "win_gray.jpg");
        End(writer);   // display
        Integer(writer, "permission", 0);
        End(writer);   // bit 0

        Node(writer, "1");
        String(writer, "name", "ACH_SECRET");
        Node(writer, "display");
        Node(writer, "name");
        String(writer, "english", "Secret");
        End(writer);
        Node(writer, "desc");
        String(writer, "english", "");
        End(writer);
        String(writer, "hidden", "1");
        String(writer, "icon", "secret.jpg");
        End(writer);   // display
        Integer(writer, "permission", 2);
        End(writer);   // bit 1

        End(writer);   // bits
        End(writer);   // stat 1

        // Old-format integer stat (type_int), which must be ignored.
        Node(writer, "2");
        Integer(writer, "type_int", 1);
        String(writer, "name", "STAT_KILLS");
        End(writer);

        End(writer);   // stats
        End(writer);   // appid
        End(writer);   // root terminator: the parser requires it
    }

    // Valve's binary KeyValues: one type byte, a NUL-terminated UTF-8 name, then the value.
    private static void Node(BinaryWriter w, string name) { w.Write((byte)0); Name(w, name); }
    private static void String(BinaryWriter w, string name, string value) { w.Write((byte)1); Name(w, name); Name(w, value); }
    private static void Integer(BinaryWriter w, string name, int value) { w.Write((byte)2); Name(w, name); w.Write(value); }
    private static void End(BinaryWriter w) => w.Write((byte)8);

    private static void Name(BinaryWriter w, string value)
    {
        w.Write(Encoding.UTF8.GetBytes(value));
        w.Write((byte)0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_steam, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
