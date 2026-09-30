using System.IO;
using System.Text;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for <see cref="EaConfigService"/>. It rewrites the emulator's config in place, so the failure
/// modes that matter are the silent ones: a value left behind, another key clobbered, or the file's
/// line endings/BOM changed under the emulator's parser.
/// </summary>
public class EaConfigServiceTests : IDisposable
{
    private const string Token = "NEW-TOKEN-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LuaToolsGui.Tests", Guid.NewGuid().ToString("N"));

    public EaConfigServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string relativePath, string content, bool bom = false)
    {
        string path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(bom));
        return path;
    }

    // ── ReplaceToken (pure) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Placeholder_IsReplacedEverywhere()
    {
        string text = $"\"A\" \"{EaConfigService.Placeholder}\"\n\"B\" \"{EaConfigService.Placeholder}\"\n";

        var (result, count) = EaConfigService.ReplaceToken(text, Token);

        Assert.Equal(2, count);
        Assert.Equal($"\"A\" \"{Token}\"\n\"B\" \"{Token}\"\n", result);
    }

    [Fact]
    public void Placeholder_WinsOverAnExistingTokenValue()
    {
        // When the placeholder is present, only it is touched; other quoted values stay as they are.
        string text = $"\"OtherToken\" \"keep\"\n\"DenuvoToken\" \"{EaConfigService.Placeholder}\"\n";

        var (result, count) = EaConfigService.ReplaceToken(text, Token);

        Assert.Equal(1, count);
        Assert.Contains("\"OtherToken\" \"keep\"", result);
        Assert.Contains($"\"DenuvoToken\" \"{Token}\"", result);
    }

    [Theory]
    [InlineData("\"DenuvoToken\"  \"old-value\"", "\"DenuvoToken\"  \"NEW-TOKEN-123\"")] // anadius.cfg style
    [InlineData("\t\"Token\"\t\"old\"", "\t\"Token\"\t\"NEW-TOKEN-123\"")]                  // indented
    [InlineData("token=\"old\"", "token=\"NEW-TOKEN-123\"")]                                // ini style
    [InlineData("Token = \"\"", "Token = \"NEW-TOKEN-123\"")]                              // empty value
    public void WithoutPlaceholder_ExistingTokenValueIsReplaced(string line, string expected)
    {
        var (result, count) = EaConfigService.ReplaceToken(line, Token);

        Assert.Equal(1, count);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WithoutPlaceholder_OtherKeysAndCommentsAreUntouched()
    {
        string text = "\"Language\" \"en_US\"\n// \"Token\" \"commented\"\n; token=\"ini comment\"\n\"GameToken\" \"old\"\n";

        var (result, count) = EaConfigService.ReplaceToken(text, Token);

        Assert.Equal(1, count);
        Assert.Equal("\"Language\" \"en_US\"\n// \"Token\" \"commented\"\n; token=\"ini comment\"\n\"GameToken\" \"NEW-TOKEN-123\"\n", result);
    }

    [Fact]
    public void WithoutPlaceholder_CrlfIsPreserved()
    {
        string text = "\"Language\" \"en_US\"\r\n\"DenuvoToken\" \"old\"\r\n\"Other\" \"x\"\r\n";

        var (result, count) = EaConfigService.ReplaceToken(text, Token);

        Assert.Equal(1, count);
        Assert.Equal("\"Language\" \"en_US\"\r\n\"DenuvoToken\" \"NEW-TOKEN-123\"\r\n\"Other\" \"x\"\r\n", result);
    }

    [Fact]
    public void NothingToReplace_ReturnsTextUnchanged()
    {
        string text = "\"Language\" \"en_US\"\n";

        var (result, count) = EaConfigService.ReplaceToken(text, Token);

        Assert.Equal(0, count);
        Assert.Equal(text, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\"quote")]
    [InlineData("has\nnewline")]
    public void InvalidToken_Throws(string token) =>
        Assert.Throws<ArgumentException>(() => EaConfigService.ReplaceToken("x", token));

    // ── ApplyToken (files) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyToken_UpdatesBothFiles_IncludingSubfolders()
    {
        string cfg = WriteFile("anadius.cfg", $"\"Config2\"\n{{\n  \"DenuvoToken\" \"{EaConfigService.Placeholder}\"\n}}\n");
        string ini = WriteFile(Path.Combine("bin", "x64", "token.ini"), "[Token]\ntoken=\"old\"\n");

        var results = new EaConfigService().ApplyToken(_dir, Token);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Changed));
        Assert.Equal($"\"Config2\"\n{{\n  \"DenuvoToken\" \"{Token}\"\n}}\n", File.ReadAllText(cfg));
        Assert.Equal($"[Token]\ntoken=\"{Token}\"\n", File.ReadAllText(ini));
    }

    [Fact]
    public void ApplyToken_MatchesFileNamesCaseInsensitively()
    {
        string cfg = WriteFile("Anadius.CFG", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"");

        var results = new EaConfigService().ApplyToken(_dir, Token);

        Assert.Single(results);
        Assert.Contains(Token, File.ReadAllText(cfg));
    }

    [Fact]
    public void ApplyToken_IgnoresOtherFiles()
    {
        string other = WriteFile("settings.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"");

        var results = new EaConfigService().ApplyToken(_dir, Token);

        Assert.Empty(results);
        Assert.Contains(EaConfigService.Placeholder, File.ReadAllText(other));
    }

    [Fact]
    public void ApplyToken_LeavesAFileWithNothingToReplaceUntouched()
    {
        string cfg = WriteFile("anadius.cfg", "\"Language\" \"en_US\"\n");
        var before = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cfg, before);

        var results = new EaConfigService().ApplyToken(_dir, Token);

        var r = Assert.Single(results);
        Assert.False(r.Changed);
        Assert.Equal(0, r.Replacements);
        Assert.Equal(before, File.GetLastWriteTimeUtc(cfg));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyToken_PreservesBomPresence(bool bom)
    {
        string cfg = WriteFile("anadius.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"", bom);

        new EaConfigService().ApplyToken(_dir, Token);

        byte[] bytes = File.ReadAllBytes(cfg);
        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.Equal(bom, hasBom);
    }

    [Fact]
    public void ApplyToken_IsIdempotentWithTheSameToken()
    {
        string cfg = WriteFile("anadius.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"\n");
        var service = new EaConfigService();

        service.ApplyToken(_dir, Token);
        var second = service.ApplyToken(_dir, Token);

        Assert.False(Assert.Single(second).Changed);
        Assert.Equal($"\"DenuvoToken\" \"{Token}\"\n", File.ReadAllText(cfg));
    }

    [Fact]
    public void ApplyToken_OnAMissingFolder_ReturnsEmpty() =>
        Assert.Empty(new EaConfigService().ApplyToken(Path.Combine(_dir, "does-not-exist"), Token));
}
