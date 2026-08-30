using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for the appmanifest reader behind the "you never launched this" warning. Steam stopped
/// recording per-game playtime in localconfig.vdf, so LastPlayed in the manifest is what separates a
/// game that has been played from one that was only installed — and zero is the value that matters.
/// </summary>
public class SteamLibraryTests
{
    private const string Manifest = """
        "AppState"
        {
        	"appid"		"1478500"
        	"Universe"		"1"
        	"name"		"Big Walk"
        	"StateFlags"		"4"
        	"installdir"		"Big Walk"
        	"LastUpdated"		"1788000000"
        	"LastPlayed"		"1788083097"
        }
        """;

    [Fact]
    public void ReadsTheStamp()
    {
        Assert.Equal(1788083097, SteamLibraryService.ReadLastPlayed(Manifest));
    }

    [Fact]
    public void ZeroMeansNeverLaunched()
    {
        Assert.Equal(0, SteamLibraryService.ReadLastPlayed(Manifest.Replace("1788083097", "0")));
    }

    [Fact]
    public void ReturnsNullWhenTheKeyIsAbsent()
    {
        // Older manifests predate the key. Unknown must stay unknown rather than read as "never".
        string withoutKey = string.Join("\n",
            Manifest.Split('\n').Where(line => !line.Contains("LastPlayed")));
        Assert.Null(SteamLibraryService.ReadLastPlayed(withoutKey));
    }

    [Fact]
    public void IsNotFooledByLastUpdated()
    {
        // The two keys sit next to each other and both end in a stamp; picking the wrong one would
        // make every installed game look played.
        Assert.NotEqual(1788000000, SteamLibraryService.ReadLastPlayed(Manifest));
    }
}
