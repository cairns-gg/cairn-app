using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Cairn.Core.Packs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cairn.App.ViewModels;

/// <summary>One world in the player's own install, and whether to bring it in.</summary>
public sealed partial class WorldRowViewModel(InstalledWorld world) : ViewModelBase
{
    public InstalledWorld World { get; } = world;

    public string Name => World.Name;

    public string Size => Bytes.Human(World.Size);

    /// <summary>"last played 3 August", which is how somebody tells two worlds apart.</summary>
    public string LastPlayed => $"last played {World.LastPlayed.ToLocalTime():d MMMM yyyy}";

    /// <summary>
    /// Off until somebody says otherwise. A world is gigabytes and the copy is theirs to
    /// ask for — and the pack works without it, which is not true of the mods.
    /// </summary>
    [ObservableProperty] public partial bool Chosen { get; set; }
}

/// <summary>
/// The worlds sitting in a plain Vintage Story install, offered to a pack.
///
/// Shared by the two places that offer them — the import dialog, and a pack's Settings tab
/// for every pack that already exists — because "which worlds are there, how big are they,
/// and what did you tick" is one question asked twice, not two questions.
/// </summary>
public sealed partial class WorldPickerViewModel : ViewModelBase
{
    public WorldPickerViewModel(string savesDir)
    {
        SavesDir = savesDir;

        foreach (var world in InstalledWorlds.Scan(savesDir))
        {
            var row = new WorldRowViewModel(world);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorldRowViewModel.Chosen)) Recount();
            };

            Worlds.Add(row);
        }
    }

    public string SavesDir { get; }

    public ObservableCollection<WorldRowViewModel> Worlds { get; } = [];

    public bool Any => Worlds.Count > 0;

    public IReadOnlyList<InstalledWorld> Chosen =>
        [.. Worlds.Where(w => w.Chosen).Select(w => w.World)];

    /// <summary>
    /// What ticking those boxes will cost, in the words of the thing that will happen: a
    /// copy, of that many bytes, leaving the originals alone.
    /// </summary>
    public string Summary
    {
        get
        {
            if (!Any) return "No worlds in your Vintage Story install.";

            var chosen = Worlds.Where(w => w.Chosen).ToList();

            if (chosen.Count == 0)
                return $"{Worlds.Count} world{(Worlds.Count == 1 ? "" : "s")} in your Vintage "
                       + "Story install. Tick any you want a copy of.";

            var bytes = chosen.Sum(w => w.World.Size);

            return $"Copying {chosen.Count} world{(chosen.Count == 1 ? "" : "s")} "
                   + $"({Bytes.Human(bytes)}). Your own copies stay where they are.";
        }
    }

    public bool HasChosen => Worlds.Any(w => w.Chosen);

    private void Recount()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasChosen));
    }
}
