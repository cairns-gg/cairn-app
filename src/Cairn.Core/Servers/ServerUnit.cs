namespace Cairn.Core.Servers;

/// <summary>Whose systemd this unit is for.</summary>
public enum UnitScope
{
    /// <summary>The machine's, running as a named user. Needs root to install.</summary>
    System,

    /// <summary>The invoking user's own, which needs linger to survive logout.</summary>
    User,
}

/// <summary>
/// The systemd unit for a Cairn-managed server, as text.
///
/// A template unit rather than one file per pack: <c>cairn-server@.service</c> is written
/// once and enabled per pack, so a box hosting three worlds has three instances of one
/// file rather than three files that will drift. %i is the pack id, which is already
/// constrained to something safe by <see cref="Packs.PackId"/> — the same guard that lets
/// it be a directory name.
///
/// Rendered here rather than in the front-end so it can be tested on a machine with no
/// systemd on it, which is every machine this is developed on.
/// </summary>
/// <para>Not translated. A unit file is systemd's format rather than prose:
/// Description= is what systemctl and journalctl print and what scripts match
/// against, so it is as fixed as the keys around it.</para>
public sealed record ServerUnit
{
    /// <summary>Where the cairn-server binary lives, as systemd will have to find it.</summary>
    public required string ExecutablePath { get; init; }

    public required UnitScope Scope { get; init; }

    /// <summary>
    /// The user a system unit runs as. Ignored for a user unit, which is already one.
    ///
    /// Cairn does not create it. Adding a system account is the kind of change that should
    /// belong to whoever administers the machine, and one Cairn made would outlive any
    /// uninstall it was part of — so the command to create it is printed instead.
    /// </summary>
    public string? User { get; init; }

    /// <summary>
    /// CAIRN_HOME for the service, when it should differ from the running user's default.
    ///
    /// Set for a system unit, where the default would be root's home or the service user's
    /// depending on who ran the install, and the state has to be somewhere deliberate.
    /// </summary>
    public string? Home { get; init; }

    public const string TemplateName = "cairn-server@.service";

    /// <summary>Where the template belongs for this scope.</summary>
    public string DirectoryPath => Scope == UnitScope.System
        ? "/etc/systemd/system"
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user");

    public string FilePath => Path.Combine(DirectoryPath, TemplateName);

    /// <summary>
    /// The unit itself.
    ///
    /// Two choices here are about not losing a world rather than about tidiness.
    /// TimeoutStopSec is long and the stop is graceful: cairn-server turns SIGTERM into the
    /// server's own /stop and waits, because a save in progress that is killed at the
    /// default 90 seconds is a rolled-back world, and "systemctl stop" is the ordinary way
    /// a server goes down. Restart=on-failure rather than always, so a server told to stop
    /// from inside the game stays stopped.
    /// </summary>
    public string Render()
    {
        var lines = new List<string>
        {
            "[Unit]",
            "Description=Vintage Story server for Cairn pack %i",
            "Documentation=https://github.com/dizzyd/cairn",
            "After=network-online.target",
            "Wants=network-online.target",
            "",
            "[Service]",
            "Type=simple",
        };

        if (Scope == UnitScope.System && !string.IsNullOrWhiteSpace(User))
        {
            lines.Add($"User={User}");
            lines.Add($"Group={User}");
        }

        if (!string.IsNullOrWhiteSpace(Home)) lines.Add($"Environment=CAIRN_HOME={Home}");

        lines.AddRange(
        [
            $"ExecStart={ExecutablePath} run %i",
            "Restart=on-failure",
            "RestartSec=10",

            // The server is asked to stop, not killed: see the summary above.
            "KillSignal=SIGTERM",
            "TimeoutStopSec=300",

            // Modest and defensible. Anything stricter has to know where CAIRN_HOME is,
            // and a hardening line that silently makes the data directory unwritable is a
            // server that will not start for a reason nobody will guess.
            "NoNewPrivileges=true",
            "PrivateTmp=true",
            "",
            "[Install]",
            Scope == UnitScope.System ? "WantedBy=multi-user.target" : "WantedBy=default.target",
            "",
        ]);

        return string.Join("\n", lines);
    }

    /// <summary>
    /// What to run once the file is written, in order, with the reason for each.
    ///
    /// Printed rather than run. Reloading a machine's systemd and enabling a service that
    /// starts at boot are the administrator's decisions, and a tool that made them while
    /// writing a file would be doing something its name did not say.
    /// </summary>
    public IReadOnlyList<string> NextSteps(string packId)
    {
        var systemctl = Scope == UnitScope.System ? "sudo systemctl" : "systemctl --user";
        var steps = new List<string>();

        if (Scope == UnitScope.System && !string.IsNullOrWhiteSpace(User))
            steps.Add($"sudo useradd --system --home-dir {Home} --create-home {User}"
                      + "    # if it does not exist yet");

        steps.Add($"{systemctl} daemon-reload");
        steps.Add($"{systemctl} enable --now cairn-server@{packId}");
        steps.Add($"{(Scope == UnitScope.System ? "sudo journalctl" : "journalctl --user")} "
                  + $"-u cairn-server@{packId} -f");

        if (Scope == UnitScope.User)
            steps.Add($"sudo loginctl enable-linger {Environment.UserName}"
                      + "    # so it survives logout and starts at boot");

        return steps;
    }
}
