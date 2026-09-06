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

    /// <summary>What this addon actually contributes, e.g. "2 sources, 1 page". Empty when nothing.</summary>
    public required string Contributes { get; init; }

    public bool Failed { get; init; }

    /// <summary>
    /// Bound to the row's switch. Setting it writes to settings immediately but changes nothing this
    /// session: assemblies cannot be safely unloaded once their types are in the visual tree and the DI
    /// container, so <see cref="RestartNeeded"/> goes up and the page says so.
    /// </summary>
    [ObservableProperty] private bool _enabled;
}

/// <summary>
/// The Addons page: what is installed, what it contributes, and what went wrong. Deliberately not a
/// store — installing is dropping a folder in, which is the whole point of a format whose smallest
/// useful form is a few lines of JSON.
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

    public ObservableCollection<AddonRow> Addons { get; } = [];

    /// <summary>Loader messages, newest last. Shown collapsed unless something failed.</summary>
    public ObservableCollection<string> Diagnostics { get; } = [];

    [ObservableProperty] private bool _restartNeeded;

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
                Id = a.Manifest.Id,
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
                RestartNeeded = true;
            };
            Addons.Add(row);
        }

        Diagnostics.Clear();
        foreach (var line in _registry.Diagnostics) Diagnostics.Add(line);

        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Describe(LoadedAddon a) => a.State switch
    {
        AddonState.Loaded => "Loaded",
        AddonState.DataOnly => "Loaded (data only, nothing executed)",
        AddonState.Disabled => "Disabled",
        _ => a.Error ?? "Failed to load",
    };

    private static string Contributions(LoadedAddon a)
    {
        List<string> parts = [];
        if (a.Sources.Count > 0) parts.Add($"{a.Sources.Count} source{(a.Sources.Count == 1 ? "" : "s")}");
        if (a.Pages.Count > 0) parts.Add($"{a.Pages.Count} page{(a.Pages.Count == 1 ? "" : "s")}");
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
