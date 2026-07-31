using System.Net;
using System.Text;
using Cairn.Core.ModDb;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// Changing a pack's game version re-resolves every mod, so it can move several at once or
/// leave one behind entirely. These cover working that out before anything is committed.
/// </summary>
public class GameVersionChangeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-retarget-" + Guid.NewGuid().ToString("n")[..8]);

    private string ModsDir => Path.Combine(_root, "Mods");
    private string LockPath => Path.Combine(_root, "pack.lock.json");

    public GameVersionChangeTests() => Directory.CreateDirectory(ModsDir);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Cdn = "https://moddbcdn.vintagestory.at";

    /// <summary>One release per mod per game-version tag, so a target can simply be absent.</summary>
    private sealed class Stub(Dictionary<string, (string Version, string[] Tags)[]> mods) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = r.RequestUri!.ToString();

            if (!url.Contains("/api/mod/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("a mod zip")),
                });

            var modId = url.Split("/api/mod/")[1].Split('?')[0];

            // ModDB answers a missing mod with HTTP 200 and a status code in the body, not
            // a 404 — and the difference decides whether this reads as a verdict or an outage.
            if (!mods.TryGetValue(modId, out var releases))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"statuscode":"404"}""", Encoding.UTF8, "application/json"),
                });

            var body = $$"""
            {"statuscode":"200","mod":{
              "modid":1,"assetid":2,"name":"{{modId}}","urlalias":"{{modId}}","side":"client",
              "releases":[{{string.Join(",", releases.Select(rel => $$"""
                {"releaseid":1,"fileid":1,"modidstr":"{{modId}}","modversion":"{{rel.Version}}",
                 "filename":"{{modId}}_{{rel.Version}}.zip",
                 "mainfile":"{{Cdn}}/{{modId}}_{{rel.Version}}.zip",
                 "tags":[{{string.Join(",", rel.Tags.Select(t => $"\"{t}\""))}}]}
              """))}}]
            }
            }
            """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ModDbClient Db, HttpClient Http) Make(
        Dictionary<string, (string Version, string[] Tags)[]> mods)
    {
        var http = new HttpClient(new Stub(mods));
        return (new ModDbClient(http), http);
    }

    private static PackManifest Pack(string gameVersion, params PackMod[] mods) => new()
    {
        Id = "anego",
        GameVersion = gameVersion,
        Mods = [.. mods],
    };

    private static PackMod Mod(string id, string? version = null) => new() { ModId = id, Version = version };

    // ---- per-mod verdicts ----

    [Fact]
    public async Task A_mod_with_a_release_for_the_target_moves_to_it()
    {
        var (db, http) = Make(new()
        {
            ["olla"] = [("1.1.0", ["1.22.5"]), ("1.2.0", ["1.22.6"])],
        });
        using var _ = http;

        var locked = new PackLock
        {
            GameVersion = "1.22.5",
            Mods = [new LockedMod { ModId = "olla", Version = "1.1.0" }],
        };

        var plan = await GameVersionChange.PreviewAsync(db, Pack("1.22.5", Mod("olla")), locked, "1.22.6");

        var olla = plan.Mods.Single();
        Assert.Equal(ModOutcome.Moves, olla.Outcome);
        Assert.Equal("1.1.0", olla.From);
        Assert.Equal("1.2.0", olla.To);
        Assert.False(plan.AnythingBreaks);
    }

    [Fact]
    public async Task A_mod_with_nothing_for_the_target_is_reported_as_breaking()
    {
        // The whole point of the preview: this is invisible until the sync fails.
        var (db, http) = Make(new()
        {
            ["olla"] = [("1.1.0", ["1.21.7"])],
        });
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(db, Pack("1.21.7", Mod("olla")), null, "1.22.6");

        var olla = plan.Mods.Single();
        Assert.Equal(ModOutcome.Unavailable, olla.Outcome);
        Assert.True(olla.Breaks);
        Assert.True(plan.AnythingBreaks);
        Assert.Contains("no release marked for 1.22.6", olla.Note);
    }

    [Fact]
    public async Task A_pinned_mod_says_the_pin_is_what_cannot_be_met()
    {
        // Distinct from "this mod is dead": unpinning it would fix this one, because 2.0.0
        // does serve the target. Note the pinned release has to be from another minor —
        // within 1.22.x the same-minor rule would let the pin stand.
        var (db, http) = Make(new()
        {
            ["olla"] = [("1.1.0", ["1.21.7"]), ("2.0.0", ["1.22.6"])],
        });
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(
            db, Pack("1.21.7", Mod("olla", "1.1.0")), null, "1.22.6");

        var olla = plan.Mods.Single();
        Assert.Equal(ModOutcome.PinUnavailable, olla.Outcome);

        // ModDB's own wording is more specific than anything reconstructed here.
        Assert.Contains("1.1.0", olla.Note);
        Assert.Contains("not marked for game 1.22.6", olla.Note);
    }

    [Fact]
    public async Task A_pin_still_stands_when_only_the_patch_level_differs()
    {
        // The counterpart to the above, and the common case on a point release: a mod
        // pinned to a 1.22.5-marked build is still what installs on 1.22.6.
        var (db, http) = Make(new()
        {
            ["olla"] = [("1.1.0", ["1.22.5"]), ("1.2.0", ["1.22.6"])],
        });
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(
            db, Pack("1.22.5", Mod("olla", "1.1.0")), null, "1.22.6");

        var olla = plan.Mods.Single();
        Assert.False(olla.Breaks);
        Assert.Equal("1.1.0", olla.To);
    }

    [Fact]
    public async Task A_release_marked_for_another_patch_in_the_same_minor_warns_rather_than_breaks()
    {
        // Vintage Story itself treats same-minor releases as installable, which is why most
        // mods survive a point release without their author touching anything.
        var (db, http) = Make(new()
        {
            ["olla"] = [("1.1.0", ["1.22.0"])],
        });
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(db, Pack("1.22.5", Mod("olla")), null, "1.22.6");

        var olla = plan.Mods.Single();
        Assert.Equal(ModOutcome.Approximate, olla.Outcome);
        Assert.False(olla.Breaks);
        Assert.Contains("1.22.x", olla.Note);
    }

    [Fact]
    public async Task A_mod_already_on_the_right_release_is_left_alone()
    {
        var (db, http) = Make(new()
        {
            ["olla"] = [("1.1.0", ["1.22.5", "1.22.6"])],
        });
        using var _ = http;

        var locked = new PackLock
        {
            GameVersion = "1.22.5",
            Mods = [new LockedMod { ModId = "olla", Version = "1.1.0" }],
        };

        var plan = await GameVersionChange.PreviewAsync(db, Pack("1.22.5", Mod("olla")), locked, "1.22.6");

        Assert.Equal(ModOutcome.Unchanged, plan.Mods.Single().Outcome);
        Assert.False(plan.AnythingBreaks);
        Assert.Empty(plan.Moving);
    }

    [Fact]
    public async Task A_mod_ModDB_has_never_heard_of_is_a_verdict_not_a_doubt()
    {
        var (db, http) = Make([]);
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(db, Pack("1.22.5", Mod("nosuchmod")), null, "1.22.6");

        var verdict = plan.Mods.Single();
        Assert.True(verdict.Breaks);
        Assert.False(plan.IsIncomplete);
    }

    [Fact]
    public async Task An_unreachable_ModDB_is_reported_as_unknown_rather_than_broken()
    {
        // "It will break" and "we could not find out" lead to different decisions, and this
        // is the screen the decision gets made on. Calling an outage a finding is a guess
        // presented as a fact.
        using var http = new HttpClient(new UnreachableHandler());

        var plan = await GameVersionChange.PreviewAsync(
            new ModDbClient(http), Pack("1.22.5", Mod("olla")), null, "1.22.6");

        var verdict = plan.Mods.Single();
        Assert.Equal(ModOutcome.Unknown, verdict.Outcome);
        Assert.False(verdict.Breaks);
        Assert.False(plan.AnythingBreaks);

        // But it must not read as "all clear" either.
        Assert.True(plan.IsIncomplete);
        Assert.Contains("could not be checked", plan.Summary());
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new HttpRequestException("no route to host");
    }

    // ---- the plan as a whole ----

    [Fact]
    public async Task Nothing_is_written_while_previewing()
    {
        var (db, http) = Make(new() { ["olla"] = [("1.2.0", ["1.22.6"])] });
        using var _ = http;

        await GameVersionChange.PreviewAsync(db, Pack("1.22.5", Mod("olla")), null, "1.22.6");

        // No download, no lockfile, no mod directory touched — this is a question, not a change.
        Assert.Empty(Directory.GetFiles(ModsDir));
        Assert.False(File.Exists(LockPath));
    }

    [Theory]
    [InlineData("1.22.5", "1.22.6", false, true)]
    [InlineData("1.22.6", "1.22.5", true, false)]
    [InlineData("1.22.5", "1.22.5", false, false)]
    [InlineData("1.22.5", "1.21.7", true, false)]
    public async Task The_direction_of_the_change_is_reported(
        string from, string to, bool downgrade, bool upgrade)
    {
        var (db, http) = Make([]);
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(db, Pack(from), null, to);

        Assert.Equal(downgrade, plan.IsDowngrade);
        Assert.Equal(upgrade, plan.IsUpgrade);
        Assert.Equal(!downgrade && !upgrade, plan.IsNoChange);
    }

    [Fact]
    public async Task Downgrading_a_pack_that_has_worlds_says_the_worlds_are_at_risk()
    {
        var (db, http) = Make([]);
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(
            db, Pack("1.22.6"), null, "1.22.5", worlds: ["My World"]);

        // The game upgrades a save's format on load without asking, and will not open a
        // save from a newer build. That cost is invisible in a version dropdown.
        Assert.True(plan.RisksWorlds);
    }

    [Fact]
    public async Task Upgrading_does_not_warn_about_worlds()
    {
        var (db, http) = Make([]);
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(
            db, Pack("1.22.5"), null, "1.22.6", worlds: ["My World"]);

        Assert.False(plan.RisksWorlds);
    }

    [Fact]
    public async Task A_pack_with_no_worlds_of_its_own_has_nothing_to_lose()
    {
        var (db, http) = Make([]);
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(db, Pack("1.22.6"), null, "1.22.5");

        Assert.True(plan.IsDowngrade);
        Assert.False(plan.RisksWorlds);
    }

    [Fact]
    public async Task The_summary_leads_with_what_would_break()
    {
        var (db, http) = Make(new()
        {
            ["olla"] = [("1.2.0", ["1.22.6"])],
            ["dead"] = [("1.0.0", ["1.21.7"])],
        });
        using var _ = http;

        var plan = await GameVersionChange.PreviewAsync(
            db, Pack("1.22.5", Mod("olla"), Mod("dead")), null, "1.22.6");

        var summary = plan.Summary();
        Assert.Contains("Upgrade 1.22.5 → 1.22.6", summary);
        Assert.Contains("1 would stop working", summary);
    }

    // ---- the preview must agree with the sync it predicts ----

    [Fact]
    public async Task What_the_preview_says_is_what_the_sync_does()
    {
        // A preview that disagrees with its sync is worse than no preview: it is the basis
        // on which the change gets committed.
        var mods = new Dictionary<string, (string, string[])[]>
        {
            ["olla"] = [("1.1.0", ["1.22.5"]), ("1.2.0", ["1.22.6"])],
            ["steady"] = [("2.0.0", ["1.22.5", "1.22.6"])],
            ["dead"] = [("1.0.0", ["1.21.7"])],
        };

        var manifest = Pack("1.22.5", Mod("olla"), Mod("steady"), Mod("dead"));

        // Settle the pack on 1.22.5 first, so the lock has something to move from.
        var (db1, http1) = Make(mods);
        using (http1) await new PackSyncer(db1, http1).SyncAsync(manifest, ModsDir, LockPath);

        var (db2, http2) = Make(mods);
        using var _ = http2;

        var plan = await GameVersionChange.PreviewAsync(
            db2, manifest, PackLock.Load(LockPath), "1.22.6");

        // Now actually do it.
        manifest.GameVersion = "1.22.6";
        var report = await new PackSyncer(db2, http2).SyncAsync(manifest, ModsDir, LockPath);

        foreach (var verdict in plan.Mods)
        {
            var failed = report.Steps.Any(
                s => s.Action == SyncAction.Failed && s.ModId == verdict.ModId);

            Assert.Equal(verdict.Breaks, failed);

            if (!verdict.Breaks)
                Assert.Equal(
                    verdict.To,
                    report.Lock.Mods.Single(m => m.ModId == verdict.ModId).Version);
        }
    }
}
