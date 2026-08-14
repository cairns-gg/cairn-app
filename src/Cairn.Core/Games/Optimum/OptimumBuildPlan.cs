namespace Cairn.Core.Games.Optimum;

public sealed class OptimumBuildException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// What enabling Optimum on a pack is about to cost, worked out before anything happens.
///
/// Exists to be shown to somebody and confirmed. Every other thing Cairn installs is a
/// download measured in minutes; this compiles a game client, and the difference is large
/// enough that starting it without saying so would be a trick. The numbers are measured
/// from a real build rather than estimated: 3.3 GB of working tree, 1 GB of finished
/// client.
/// </summary>
public sealed record OptimumBuildPlan
{
    /// <summary>Tools the machine is missing. Nothing can start until this is empty.</summary>
    public required PrereqReport Prereqs { get; init; }

    /// <summary>Whether a .NET SDK has to be downloaded first.</summary>
    public required bool NeedsSdk { get; init; }

    /// <summary>Whether this build is already installed and could just be used.</summary>
    public required bool AlreadyBuilt { get; init; }

    /// <summary>The version this build produces, and the one it is for.</summary>
    public required OptimumSource Source { get; init; }

    /// <summary>Free space on the volume the work happens on, or -1 when unreadable.</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Measured from a real build: working tree, finished client, and the SDK.</summary>
    public const long BuildTreeBytes = 3_300L * 1024 * 1024;
    public const long InstalledBytes = 1_000L * 1024 * 1024;
    public const long SdkBytes = 1_000L * 1024 * 1024;

    /// <summary>
    /// The redistributable the packager makes alongside the client directory — a 604 MB
    /// tarball on Linux, a 700 MB disk image on macOS.
    ///
    /// Counted because it exists at the same time as the folder it was made from, so it is
    /// part of the peak even though Cairn deletes it moments later. Leaving it out
    /// understated a real Linux build by most of a gigabyte, on a machine that finished
    /// with 1.5 GB to spare. Windows' packager is asked for a folder and makes no archive,
    /// but the allowance is harmless there and a wrong "yes, there is room" is not.
    /// </summary>
    public static long RedistributableBytes =>
        System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? 0
            : 700L * 1024 * 1024;

    /// <summary>Everything this will occupy at its peak.</summary>
    public long RequiredBytes =>
        BuildTreeBytes + InstalledBytes + RedistributableBytes + (NeedsSdk ? SdkBytes : 0);

    /// <summary>
    /// Whether there is room, with headroom.
    ///
    /// A build that fills the disk does not merely fail — it fails after twenty minutes,
    /// having taken the machine's free space with it, and a decompiler running out of room
    /// reports something unrelated to the actual problem.
    /// </summary>
    public bool EnoughSpace => FreeBytes < 0 || FreeBytes > RequiredBytes + (500L * 1024 * 1024);

    public bool CanStart => Prereqs.Satisfied && EnoughSpace;

    /// <summary>
    /// The warning, in the words somebody needs to decide.
    ///
    /// Time is given as a range because it is a compile: it depends on the machine, and a
    /// single confident number that turns out to be half the truth is worse than a range.
    /// </summary>
    public string Describe()
    {
        if (!Prereqs.Satisfied) return Prereqs.Describe();

        var lines = new List<string>
        {
            Lang.Get("optimum-plan-intro", Source.Version, Source.GameVersion),
            "",
            Lang.Get("optimum-plan-compile"),
            "  • " + Lang.Get("optimum-plan-time"),
            "  • " + Lang.Get("optimum-plan-disk", Gb(RequiredBytes)),
            "  • " + Lang.Get("optimum-plan-sizes", Gb(InstalledBytes), Gb(BuildTreeBytes)),
        };

        if (NeedsSdk) lines.Add("  • " + Lang.Get("optimum-plan-sdk", Gb(SdkBytes)));

        lines.Add("");
        lines.Add(Lang.Get("optimum-plan-cancellable"));

        if (!EnoughSpace)
        {
            lines.Add("");
            lines.Add(Lang.Get("optimum-plan-no-space", Gb(FreeBytes), Gb(RequiredBytes)));
        }

        return string.Join("\n", lines);
    }

    private static string Gb(long bytes) => bytes < 0
        ? Lang.Get("optimum-plan-unknown-size")
        : $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
}
