using Cairn.Core.Games.Optimum;

namespace Cairn.Core.Games;

/// <summary>Why a directory cannot be used as a client, when it cannot.</summary>
public enum AdoptionProblem
{
    None,

    /// <summary>No Vintage Story install there, or one level below it.</summary>
    NotAnInstall,

    /// <summary>An install, but the stock game rather than a modified client.</summary>
    NoLauncher,

    /// <summary>An install whose assembly names no version, so nothing can match it.</summary>
    NoVersion,

    /// <summary>A modified client for a game version other than the one asked about.</summary>
    WrongVersion,
}

/// <param name="Problem">
/// <see cref="AdoptionProblem.None"/> when it can be used, and then <paramref name="Client"/>
/// and <paramref name="Install"/> are both set.
/// </param>
/// <param name="Message">What to tell whoever picked the directory. Always set.</param>
public sealed record AdoptionResult(
    AdoptionProblem Problem, string Message,
    ExternalClient? Client = null, GameInstall? Install = null)
{
    public bool Ok => Problem == AdoptionProblem.None;
}

/// <summary>
/// Whether a directory somebody picked is a client Cairn can be pointed at, and what it is.
///
/// In Core rather than in the folder picker, because the answer is a policy question and
/// both front-ends ask it — a check that lives in a view model is a check the CLI does not
/// make. Every refusal here is one of the ways a wrong answer would otherwise surface
/// twenty minutes later as "Cairn says Optimum and the game says vanilla".
/// </summary>
public static class ClientAdoption
{
    /// <summary>What Cairn calls a client built from Optimum's sources.</summary>
    public const string OptimumLabel = "Optimum";

    /// <param name="directory">
    /// What was picked. Searched one level down as well, because on macOS a folder picker
    /// cannot be used to select a <c>.app</c> and the only thing that can be chosen is the
    /// folder holding it — the same allowance <see cref="GameInstall.Choose"/> makes.
    /// </param>
    /// <param name="gameVersion">
    /// The version the pack in hand targets, so a build for another one is refused at the
    /// picker rather than recorded and then silently ignored at launch. Null asks only
    /// whether this is a usable client at all, which is what the games list wants.
    /// </param>
    public static AdoptionResult Inspect(string directory, string? gameVersion)
    {
        // Read without a declared variant on purpose: this is the question of what the
        // directory *is*, and answering it partly from what somebody said it was would let
        // a stale record vouch for a tree that has since been rebuilt into something else.
        if (GameInstall.Choose(directory) is not { } found)
            return new AdoptionResult(AdoptionProblem.NotAnInstall,
                Lang.Get("adopt-not-an-install", directory));

        return Inspect(found, directory, gameVersion);
    }

    /// <summary>
    /// The rules alone, over an install already located.
    ///
    /// Split from the lookup because they are two different questions and only one of them
    /// is policy. Finding an install means reading a version out of a compiled assembly,
    /// which nothing but a real game install has — so with them fused, every rule below
    /// could only be exercised against somebody's actual Vintage Story directory.
    /// </summary>
    /// <param name="picked">What was originally picked, for a message that names it.</param>
    public static AdoptionResult Inspect(GameInstall found, string picked, string? gameVersion)
    {
        var dir = found.Directory;

        if (OptimumSource.FindLauncher(dir) is not { } launcher)
            return new AdoptionResult(AdoptionProblem.NoLauncher,
                Lang.Get("adopt-no-launcher", dir));

        // A client whose version cannot be read matches no pack, so recording it would be
        // recording a choice that can never apply. Said here, where the directory is in
        // front of somebody, rather than as a pack that quietly runs the stock game.
        if (!GameVersions.IsPlausibleVersion(found.Version))
            return new AdoptionResult(AdoptionProblem.NoVersion,
                Lang.Get("adopt-no-version", dir));

        if (gameVersion is not null
            && !string.Equals(found.Version, gameVersion, StringComparison.OrdinalIgnoreCase))
            return new AdoptionResult(AdoptionProblem.WrongVersion,
                Lang.Get("adopt-wrong-version", found.Version, gameVersion));

        var client = new ExternalClient(dir, OptimumLabel, launcher);

        // Rebuilt through the variant it will actually be read as, so what the confirmation
        // shows is what will run rather than a description of it.
        var install = GameInstall.TryAt(dir, new VariantSpec(client.Label, client.Executable));

        if (install is null)
            return new AdoptionResult(AdoptionProblem.NotAnInstall,
                Lang.Get("adopt-not-an-install", picked));

        // found's version rather than the re-read one: they are the same value in every real
        // case, and this is the one the rules above were applied to. Reporting a second
        // reading would let the confirmation name a version the check did not use.
        return new AdoptionResult(AdoptionProblem.None,
            Lang.Get("adopt-found", dir, found.Version, launcher), client, install);
    }
}
