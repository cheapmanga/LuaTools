using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for the localconfig.vdf reader behind the "you have barely played this" warning. The file
/// belongs to Steam, so what matters is tolerating its shape: an app id shows up in more than one
/// section, only one of which carries the playtime, and the blocks nest.
/// </summary>
public class SteamPlaytimeTests
{
    // Trimmed to the shape that matters: the app appears twice, once in a section with no playtime.
    private const string LocalConfig = """
        "UserLocalConfigStore"
        {
        	"Software"
        	{
        		"Valve"
        		{
        			"Steam"
        			{
        				"apps"
        				{
        					"480"
        					{
        						"LastPlayed"		"1756500000"
        						"Playtime2wks"		"12"
        						"Playtime"		"143"
        						"BadgeData"
        						{
        							"Level"		"1"
        						}
        					}
        					"570"
        					{
        						"LastPlayed"		"1756400000"
        						"Playtime"		"9021"
        					}
        				}
        			}
        		}
        	}
        	"depots"
        	{
        		"480"
        		{
        			"manifest"		"1234567890"
        		}
        	}
        }
        """;

    [Fact]
    public void ReadsPlaytimeForTheRightGame()
    {
        Assert.Equal(143, SteamPlaytimeService.ReadPlaytime(LocalConfig, 480));
        Assert.Equal(9021, SteamPlaytimeService.ReadPlaytime(LocalConfig, 570));
    }

    [Fact]
    public void SkipsBlocksThatCarryNoPlaytime()
    {
        // 480 also appears under "depots" with no Playtime; the reader must walk past it rather than
        // stop at the first block bearing the app id.
        const string depotsFirst = """
            "UserLocalConfigStore"
            {
            	"depots"
            	{
            		"480"
            		{
            			"manifest"		"1234567890"
            		}
            	}
            	"apps"
            	{
            		"480"
            		{
            			"Playtime"		"77"
            		}
            	}
            }
            """;
        Assert.Equal(77, SteamPlaytimeService.ReadPlaytime(depotsFirst, 480));
    }

    [Fact]
    public void ReturnsNullWhenTheGameWasNeverPlayed()
    {
        Assert.Null(SteamPlaytimeService.ReadPlaytime(LocalConfig, 999999));
    }

    [Fact]
    public void ReturnsNullOnATruncatedFile()
    {
        // Steam was killed mid-write. Better no warning than a wrong one.
        Assert.Null(SteamPlaytimeService.ReadPlaytime("\"apps\"\n{\n\t\"480\"\n{\n\t\"Playtime\"", 480));
    }

    [Fact]
    public void AccountIdIsTheSteamIdMinusItsBase()
    {
        Assert.Equal(123456789, SteamPlaytimeService.AccountIdFrom(76561198083722517));
    }
}
