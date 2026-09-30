using System.IO;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The "EA token" tab. What's pinned here is what the user sees before and after clicking Write: the
/// files found in the folder, the button only enabled when writing can do something, and a status that
/// tells a real write apart from a folder with nothing to replace.
/// </summary>
public class EaTokenViewModelTests : IDisposable
{
    private const string Token = "NEW-TOKEN-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LuaToolsGui.Tests", Guid.NewGuid().ToString("N"));
    private readonly EaTokenViewModel _vm = new(new EaConfigService(), new ToastService());

    public EaTokenViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task SetFolder(string folder)
    {
        _vm.GameFolder = folder;
        await _vm.Scan;
    }

    [Fact]
    public async Task SettingTheFolder_ListsTheConfigFilesRelativeToIt()
    {
        WriteFile("anadius.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"");
        WriteFile(Path.Combine("bin", "token.ini"), $"token=\"{EaConfigService.Placeholder}\"");

        await SetFolder(_dir);

        Assert.Equal(["anadius.cfg", Path.Combine("bin", "token.ini")], _vm.FoundFiles);
        Assert.Contains("anadius.cfg", _vm.FoundText);
    }

    [Fact]
    public async Task AFolderWithoutConfigs_SaysSo_AndCantApply()
    {
        await SetFolder(_dir);
        _vm.Token = Token;

        Assert.Empty(_vm.FoundFiles);
        Assert.Equal(Strings.Ea_NotFound, _vm.FoundText);
        Assert.False(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_NeedsATokenAndAFolderWithConfigs()
    {
        WriteFile("anadius.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"");

        Assert.False(_vm.ApplyCommand.CanExecute(null)); // nothing set
        await SetFolder(_dir);
        Assert.False(_vm.ApplyCommand.CanExecute(null)); // no token yet
        _vm.Token = "   ";
        Assert.False(_vm.ApplyCommand.CanExecute(null)); // blank token
        _vm.Token = Token;
        Assert.True(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_WritesTheTrimmedToken_AndReportsSuccess()
    {
        string cfg = WriteFile("anadius.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"\n");
        await SetFolder(_dir);
        _vm.Token = $"  {Token}\n";

        await _vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal($"\"DenuvoToken\" \"{Token}\"\n", File.ReadAllText(cfg));
        Assert.False(_vm.StatusIsError);
        Assert.Equal(string.Format(Strings.Ea_Status_Done, 1), _vm.Status);
    }

    [Fact]
    public async Task Apply_WithNothingToReplace_IsReportedAsAnError()
    {
        WriteFile("anadius.cfg", "\"Language\" \"en_US\"\n");
        await SetFolder(_dir);
        _vm.Token = Token;

        await _vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(_vm.StatusIsError);
        Assert.Equal(string.Format(Strings.Ea_Status_NothingToReplace, 1), _vm.Status);
    }

    [Fact]
    public async Task Apply_WithAQuoteInTheToken_IsRejectedWithoutTouchingTheFile()
    {
        string content = $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"\n";
        string cfg = WriteFile("anadius.cfg", content);
        await SetFolder(_dir);
        _vm.Token = "bad\"token";

        await _vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(_vm.StatusIsError);
        Assert.Equal(Strings.Ea_Status_Invalid, _vm.Status);
        Assert.Equal(content, File.ReadAllText(cfg));
    }

    [Fact]
    public async Task Apply_OnAReadOnlyFile_ReportsTheFailure()
    {
        string cfg = WriteFile("anadius.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"\n");
        await SetFolder(_dir);
        _vm.Token = Token;
        File.SetAttributes(cfg, FileAttributes.ReadOnly);

        try
        {
            await _vm.ApplyCommand.ExecuteAsync(null);

            Assert.True(_vm.StatusIsError);
            Assert.StartsWith(string.Format(Strings.Ea_Status_Failed, "").TrimEnd(), _vm.Status);
            Assert.False(_vm.IsApplying);
        }
        finally
        {
            File.SetAttributes(cfg, FileAttributes.Normal); // or Dispose can't delete it
        }
    }

    [Fact]
    public async Task ChangingTheFolder_ClearsTheLastStatus()
    {
        WriteFile("anadius.cfg", $"\"DenuvoToken\" \"{EaConfigService.Placeholder}\"\n");
        await SetFolder(_dir);
        _vm.Token = Token;
        await _vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(_vm.HasStatus);

        await SetFolder(Path.Combine(_dir, "elsewhere"));

        Assert.False(_vm.HasStatus);
        Assert.Empty(_vm.FoundFiles);
    }
}
