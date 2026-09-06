using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Addons;

namespace LuaToolsGui.ViewModels;

/// <summary>One row of the Addons list.</summary>
public partial class AddonRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public required string Directory { get; init; }

    /// <summary>"Loaded" / "Data only" / "Disabled" / the failure reason. One line, for the user.</summary>
    public required string Status { get; init; }

    /// <summary>What this addon actually contributes, e.g. "2 sources". Empty when nothing.</summary>
    public required string Contributes { get; init; }

    public bool Failed { get; init; }

    /// <summary>Bound to the row's switch. Takes effect from the next fetch; nothing needs restarting.</summary>
    [ObservableProperty] private bool _enabled;
}

/// <summary>
/// The Addons page: what is installed, what it contributes, and what went wrong. Deliberately not a
/// store — installing is dropping a folder in, which is the whole point of a format that is only ever
/// a few lines of JSON and can never carry code.
/// </summary>
public partial class AddonsViewModel : ObservableObject
{
    private readonly AddonRegistry _registry;
    private readonly SettingsService _settings;

    public AddonsViewModel(AddonRegistry registry, SettingsService settings)
    {
        _registry = registry;
        _settings = settings;
        Reload();
    }

    /// <summary>
    /// Re-read the addon folder and show what is there now. Called when the page is opened and by the
    /// Refresh button, so dropping an addon in and coming back to this page is enough to see it. An
    /// addon is only ever data, so there is nothing a restart could do that this does not.
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        _registry.Reload(_settings.DisabledAddons);
        Reload();
    }

    public ObservableCollection<AddonRow> Addons { get; } = [];

    /// <summary>Loader messages, newest last. Shown collapsed unless something failed.</summary>
    public ObservableCollection<string> Diagnostics { get; } = [];

    /// <summary>True when there is nothing to list, so the page can explain how to add one.</summary>
    public bool IsEmpty => Addons.Count == 0;

    public string AddonsFolder => AddonRegistry.Root;

    private void Reload()
    {
        Addons.Clear();
        var disabled = _settings.DisabledAddons;

        foreach (var a in _registry.Addons)
        {
            var row = new AddonRow
            {
                // The folder, not the manifest's claim - see LoadedAddon.FolderName. It keys the
                // disabled list, so a refused addon can still be switched off by the folder it is in.
                Id = a.FolderName,
                Name = string.IsNullOrWhiteSpace(a.Manifest.Name) ? a.Manifest.Id : a.Manifest.Name,
                Version = a.Manifest.Version,
                Author = a.Manifest.Author,
                Description = a.Manifest.Description,
                Directory = a.Directory,
                Status = Describe(a),
                Contributes = Contributions(a),
                Failed = a.State == AddonState.Failed,
                Enabled = !disabled.Contains(a.Manifest.Id, StringComparer.OrdinalIgnoreCase),
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(AddonRow.Enabled)) return;
                _settings.SetAddonEnabled(row.Id, row.Enabled);
                Refresh();
            };
            Addons.Add(row);
        }

        Diagnostics.Clear();
        foreach (var line in _registry.Diagnostics) Diagnostics.Add(line);

        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Describe(LoadedAddon a) => a.State switch
    {
        AddonState.Loaded => Resources.Strings.Addons_State_Loaded,
        AddonState.Disabled => Resources.Strings.Addons_State_Disabled,
        // The loader's reason, when there is one, beats a generic label: it names the actual problem.
        _ => a.Error ?? Resources.Strings.Addons_State_Failed,
    };

    private static string Contributions(LoadedAddon a)
    {
        List<string> parts = [];
        if (a.Sources.Count > 0) parts.Add($"{a.Sources.Count} source{(a.Sources.Count == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }

    /// <summary>Opens the addons folder, creating it first so the user never lands on a missing path.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(AddonRegistry.Root);
            Process.Start(new ProcessStartInfo(AddonRegistry.Root) { UseShellExecute = true });
        }
        catch { /* opening a folder is never worth an error dialog */ }
    }
}
