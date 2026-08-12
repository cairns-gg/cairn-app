
using Cairn.Core;
using Cairn.Core.Launch;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The files that hold a credential, and the mode they land at.
///
/// session.json is the Vintage Story login — a session key, an mptoken, an entitlements
/// blob — and every pack's clientsettings.json receives the same keys at launch so one
/// sign-in reaches all of them. Both were written with no mode set, so they arrived at
/// 0644 under an ordinary umask, inside a directory created at 0755. On macOS a home
/// directory is world-traversable by default, which makes that readable by any other
/// account on the machine for as long as the file exists.
///
/// Unix only. On Windows these APIs do nothing and the profile ACL is what keeps other
/// standard users out, so there is nothing here to assert.
/// </summary>
public class OwnerOnlyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-mode-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path);

    [Fact]
    public void A_session_file_is_readable_only_by_the_person_it_belongs_to()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_root, "session.json");

        new ClientSession { Values = { ["sessionkey"] = "not-a-real-key" } }.Save(path);

        Assert.True(File.Exists(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(path));

        // And the directory around it, so containment covers whatever is written next.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            ModeOf(_root));
    }

    [Fact]
    public void A_packs_settings_file_is_too_because_the_login_is_merged_into_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_root, "data", "clientsettings.json");

        // sessionkey as well as the token: a session without one is treated as empty and
        // merges nothing, which would make this pass for the wrong reason.
        new ClientSession
        {
            Values = { ["sessionkey"] = "not-a-real-key", ["mptoken"] = "not-a-real-token" },
        }.MergeInto(path);

        Assert.Contains("not-a-real-token", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(path));
    }

    [Fact]
    public void A_file_left_behind_by_an_older_build_is_narrowed_when_it_is_rewritten()
    {
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");

        // As a previous version would have left it. A create mode applies only when the
        // file is created, so rewriting one that already exists has to narrow it too —
        // otherwise a token written once stays world-readable for ever.
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                   | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        new ClientSession { Values = { ["sessionkey"] = "not-a-real-key" } }.MergeInto(path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(path));
    }

    [Fact]
    public void The_staging_file_is_never_wider_than_the_one_it_becomes()
    {
        if (OperatingSystem.IsWindows()) return;

        // The window that mattered: writing at 0644 and narrowing afterwards leaves the
        // credential on disk readable in between, and a descriptor opened during it keeps
        // its access across the change. Asserted on OwnerOnly directly, since the staging
        // file inside a save is gone by the time anything else could look.
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "fresh.json");

        OwnerOnly.WriteText(path, "{}");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(path));
    }
}
