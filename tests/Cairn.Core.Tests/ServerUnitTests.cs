using Cairn.Core.Servers;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The systemd unit, checked on machines that have no systemd — which is every machine this
/// is developed on, and why the text is rendered in Core rather than by the front-end.
/// </summary>
public class ServerUnitTests
{
    private static ServerUnit System() => new()
    {
        ExecutablePath = "/usr/local/bin/cairn-server",
        Scope = UnitScope.System,
        User = "cairn",
        Home = "/var/lib/cairn",
    };

    private static ServerUnit User() => new()
    {
        ExecutablePath = "/home/dizzyd/bin/cairn-server",
        Scope = UnitScope.User,
    };

    [Fact]
    public void One_template_serves_every_pack_on_the_box()
    {
        // %i is the pack id, so three worlds are three instances of one file rather than
        // three files that will drift apart.
        Assert.Equal("cairn-server@.service", ServerUnit.TemplateName);
        Assert.Contains("ExecStart=/usr/local/bin/cairn-server run %i", System().Render());
        Assert.Contains("Description=Vintage Story server for Cairn pack %i", System().Render());
    }

    [Fact]
    public void A_system_unit_runs_as_its_own_user_from_its_own_state_directory()
    {
        var text = System().Render();

        Assert.Contains("User=cairn", text);
        Assert.Contains("Group=cairn", text);
        Assert.Contains("Environment=CAIRN_HOME=/var/lib/cairn", text);
        Assert.Contains("WantedBy=multi-user.target", text);
        Assert.Equal("/etc/systemd/system/cairn-server@.service", System().FilePath);
    }

    [Fact]
    public void A_user_unit_is_already_a_user_and_says_neither()
    {
        var text = User().Render();

        // User= in a --user unit is refused by systemd, and CAIRN_HOME is already the one
        // the person's own tools use.
        Assert.DoesNotContain("User=", text);
        Assert.DoesNotContain("CAIRN_HOME", text);
        Assert.Contains("WantedBy=default.target", text);
        Assert.Contains(".config/systemd/user", User().FilePath);
    }

    [Fact]
    public void Stopping_is_given_time_to_save_rather_than_killed()
    {
        var text = System().Render();

        // systemd's default is to give up after 90 seconds and SIGKILL. A world being
        // saved when that lands is a world rolled back to its last save, and "systemctl
        // stop" is the ordinary way a server goes down.
        Assert.Contains("KillSignal=SIGTERM", text);
        Assert.Contains("TimeoutStopSec=300", text);

        // on-failure, not always: a server told to stop from inside the game stays stopped.
        Assert.Contains("Restart=on-failure", text);
        Assert.DoesNotContain("Restart=always", text);
    }

    [Fact]
    public void The_steps_afterwards_are_printed_rather_than_run()
    {
        var steps = System().NextSteps("mypack");

        Assert.Contains(steps, s => s.Contains("useradd") && s.Contains("cairn"));
        Assert.Contains(steps, s => s.Contains("daemon-reload"));
        Assert.Contains(steps, s => s.Contains("enable --now cairn-server@mypack"));
        Assert.Contains(steps, s => s.Contains("journalctl"));
    }

    [Fact]
    public void A_user_unit_is_told_about_linger()
    {
        // Without it the service stops at logout and never starts at boot, which looks
        // exactly like a unit that was never enabled.
        Assert.Contains(User().NextSteps("mypack"), s => s.Contains("enable-linger"));

        // And it is not a system unit's problem.
        Assert.DoesNotContain(System().NextSteps("mypack"), s => s.Contains("enable-linger"));
    }

    [Fact]
    public void A_user_unit_never_asks_for_root()
    {
        Assert.All(
            User().NextSteps("mypack").Where(s => !s.Contains("enable-linger")),
            s => Assert.DoesNotContain("sudo", s));
    }
}
