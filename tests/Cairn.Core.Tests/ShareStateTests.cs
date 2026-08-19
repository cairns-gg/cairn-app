using System.Text.Json.Nodes;
using Cairn.Core.Launch;
using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What the Share button offers, which is a projection of the pack rather than stored
/// state. The interesting cases are the ones where a naive comparison would be wrong: a
/// pack published with its server address stripped, and a pack being followed.
/// </summary>
public class ShareStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cairn-share-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;

    public ShareStateTests()
    {
        _store = new PackStore(_root);
        var manifest = _store.Create("anego", "1.22.5", "Anego Server", "host:42420");
        manifest.Mods.Add(new PackMod { ModId = "glassview" });
        _store.Save(manifest);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Publish(string connect = "stripped")
    {
        _store.SaveLink("anego", new PackLink
        {
            Role = PackRole.Author,
            Url = "https://cairns.gg/dizzyd/anego",
            Revision = 1,
            Published = new PublishRecord
            {
                Connect = connect,
                Visibility = "unlisted",
                Fingerprint = PackLink.Fingerprint(
                    _store.PublishedDocument("anego", stripConnect: connect == "stripped")),
            },
        });
    }

    [Fact]
    public void A_pack_that_was_never_published_offers_to_share()
    {
        var state = _store.ShareStateFor("anego");

        Assert.Equal(ShareStatus.Unshared, state.Status);
        Assert.Equal("Share…", state.Label);
        Assert.False(state.IsUrgent);
        Assert.False(state.HasUrl);
    }

    [Fact]
    public void A_published_pack_with_nothing_pending_just_says_shared()
    {
        Publish();

        var state = _store.ShareStateFor("anego");

        Assert.Equal(ShareStatus.Shared, state.Status);
        Assert.Equal("Shared", state.Label);

        // Not accented: nothing is outstanding, and Play is what the row is for.
        Assert.False(state.IsUrgent);
        Assert.Equal("https://cairns.gg/dizzyd/anego", state.Url);
    }

    [Fact]
    public void Changing_the_pack_turns_the_button_into_publish_changes()
    {
        Publish();

        var manifest = _store.Load("anego");
        manifest.Mods.Add(new PackMod { ModId = "unchisel" });
        _store.Save(manifest);

        var state = _store.ShareStateFor("anego");

        Assert.Equal(ShareStatus.Pending, state.Status);
        Assert.Equal("Publish changes", state.Label);
        Assert.True(state.IsUrgent);
    }

    [Fact]
    public void A_stripped_server_address_is_not_a_pending_change()
    {
        // The pack has a connect address and was published without it, so the local pack
        // and the published document differ permanently. Comparing them directly would
        // report changes to publish forever, on a pack nobody had touched.
        Publish(connect: "stripped");

        Assert.Equal(ShareStatus.Shared, _store.ShareStateFor("anego").Status);
    }

    [Fact]
    public void Changing_the_server_address_of_a_pack_published_with_it_does_pend()
    {
        Publish(connect: "included");

        var manifest = _store.Load("anego");
        manifest.Connect = "elsewhere:42420";
        _store.Save(manifest);

        Assert.Equal(ShareStatus.Pending, _store.ShareStateFor("anego").Status);
    }

    [Fact]
    public void A_pack_being_followed_does_not_offer_to_share_at_all()
    {
        _store.SaveLink("anego", new PackLink
        {
            Role = PackRole.Follower,
            Url = "https://cairns.gg/dizzyd/anego",
            Revision = 4,
            Following = true,
        });

        var state = _store.ShareStateFor("anego");

        // Publishing a pack you follow is republishing someone else's curation under your
        // own name. Take over comes first, so the button is not offered rather than
        // offered-and-refused.
        Assert.Equal(ShareStatus.Following, state.Status);
        Assert.False(state.IsOffered);
    }

    [Fact]
    public void Taking_over_a_followed_pack_does_not_make_it_published()
    {
        _store.SaveLink("anego", new PackLink
        {
            Role = PackRole.Follower,
            Url = "https://cairns.gg/dizzyd/anego",
            Revision = 4,
            Following = false,
        });

        var state = _store.ShareStateFor("anego");

        // It still points at where it came from, which is not a URL its new owner can
        // update. Sharing it is a fresh publish under a URL of their own.
        Assert.Equal(ShareStatus.Unshared, state.Status);
        Assert.True(state.IsOffered);
    }

    /// <summary>What a withdrawal leaves behind: the address, without the publish record.</summary>
    private void Withdraw() => _store.MarkWithdrawn("anego");

    [Fact]
    public void Marking_a_pack_withdrawn_keeps_its_address()
    {
        Publish();
        _store.MarkWithdrawn("anego");

        var link = _store.LoadLink("anego")!;

        // The URL is still theirs — the server revives the pack there, and it is what the
        // next publish defaults to. Losing it would turn coming back into starting again
        // somewhere new.
        Assert.Equal("https://cairns.gg/dizzyd/anego", link.Url);
        Assert.True(link.Withdrawn);
        Assert.Null(link.Published);
    }

    [Fact]
    public void Marking_a_pack_that_was_never_published_does_nothing()
    {
        _store.MarkWithdrawn("anego");

        // No link to move, and inventing one would give an unshared pack an address it
        // never had.
        Assert.Null(_store.LoadLink("anego"));
        Assert.Equal(ShareStatus.Unshared, _store.ShareStateFor("anego").Status);
    }

    [Fact]
    public void A_withdrawn_pack_is_not_the_same_as_one_never_shared()
    {
        Publish();
        Withdraw();

        var state = _store.ShareStateFor("anego");

        // The row survives on the site and the URL answers 410, so the address is still
        // this pack's — and publishing again revives it there rather than starting over.
        Assert.Equal(ShareStatus.Withdrawn, state.Status);
        Assert.Equal("Publish again", state.Label);
        Assert.Equal("https://cairns.gg/dizzyd/anego", state.Url);
        Assert.True(state.IsOffered);
        Assert.False(state.IsUrgent);
    }

    [Fact]
    public void A_withdrawn_pack_can_be_published_again_unchanged()
    {
        Publish();
        var record = _store.LoadLink("anego")!.Published!;
        var document = _store.PublishedDocument("anego", stripConnect: true);
        Withdraw();

        // The document is untouched, so that record would have refused this as a revision
        // differing from its predecessor in nothing but its number — which is right in
        // general and wrong for a pack that is down. Clearing it is what leaves both
        // front-ends nothing to compare against.
        Assert.False(record.WouldChange(document, @public: false, strip: true));
        Assert.Null(_store.LoadLink("anego")!.Published);
    }

    [Fact]
    public void Taking_over_a_pack_is_not_mistaken_for_withdrawing_one()
    {
        _store.SaveLink("anego", new PackLink
        {
            Role = PackRole.Author,
            Url = "https://cairns.gg/someone/anego",
            Revision = 4,
        });

        // Author, a URL, nothing published — the same shape a withdrawal leaves, which is
        // why the withdrawal is recorded rather than inferred. This one never had an
        // address of its own; it points at where it came from.
        Assert.Equal(ShareStatus.Unshared, _store.ShareStateFor("anego").Status);
    }

    [Fact]
    public void The_fingerprint_covers_the_lock_as_well_as_the_manifest()
    {
        Publish();

        // A mod moving is a change worth republishing even though the manifest, which
        // names mods without versions, reads identically before and after.
        new PackLock
        {
            GameVersion = "1.22.5",
            Mods = [new LockedMod { ModId = "glassview", Version = "1.3.1" }],
        }.Save(_store.LockPath("anego"));

        Assert.Equal(ShareStatus.Pending, _store.ShareStateFor("anego").Status);
    }

    private static PublishRecord Sent(string document, bool @public = false, bool strip = true) =>
        new()
        {
            Fingerprint = PackLink.Fingerprint(document),
            Visibility = @public ? "public" : "unlisted",
            Connect = strip ? "stripped" : "included",
        };

    [Fact]
    public void Republishing_the_same_document_with_the_same_choices_sends_nothing_new()
    {
        // A revision differing from its predecessor in nothing but its number tells every
        // follower there is an update and then has none for them.
        Assert.False(Sent("{\"pack\":1}")
            .WouldChange("{\"pack\":1}", @public: false, strip: true));
    }

    [Fact]
    public void An_unlisted_pack_says_so()
    {
        Publish();

        var state = _store.ShareStateFor("anego");

        // Unlisted and public look identical from outside, and which one a pack is decides
        // whether handing the link around is sharing it or publishing it.
        Assert.Equal(ShareStatus.Shared, state.Status);
        Assert.True(state.IsUnlisted);
    }

    [Fact]
    public void Changed_bytes_are_a_change() =>
        Assert.True(Sent("{\"pack\":1}")
            .WouldChange("{\"pack\":2}", @public: false, strip: true));

    [Theory]
    [InlineData(true, true)]    // unlisted -> public
    [InlineData(false, false)]  // stripped -> included
    public void And_so_are_the_choices_even_when_the_bytes_are_identical(bool @public, bool strip)
    {
        // Why the check is not the fingerprint alone: going public is a real change with
        // nothing to show for it in the document, and refusing it would strand somebody
        // whose only remaining edit is the one this field controls.
        Assert.True(Sent("{\"pack\":1}").WouldChange("{\"pack\":1}", @public, strip));
    }

    // ---- a carried setting that moves after it was published ----

    private void WriteConfig(string json)
    {
        var path = Path.Combine(
            ModConfigFiles.DirectoryIn(_store.DataDir("anego")), "terrainslabs.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>The pack carries one setting, at whatever the file currently says.</summary>
    private void Carry(string value)
    {
        WriteConfig($$"""{ "compatibleMods": {{value}} }""");

        var manifest = _store.Load("anego");
        manifest.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] =
                (JsonNode.Parse($$"""{"compatibleMods":{{value}}}""") as JsonObject)!,
        };
        _store.Save(manifest);
    }

    /// <summary>
    /// A published pack notices that a carried setting has moved.
    ///
    /// The document is what ShareState compares, and it was built from the manifest alone —
    /// so a value changed after being ticked left the pack reporting itself as published and
    /// up to date, with the old number still in it. There was nothing on screen to disagree
    /// with, which is what made it hard to see.
    /// </summary>
    [Fact]
    public void Changing_a_carried_setting_gives_the_pack_something_to_publish()
    {
        Carry("""["footprints"]""");
        Publish();

        Assert.Equal(ShareStatus.Shared, _store.ShareStateFor("anego").Status);

        // In game, afterwards. The tick already said this value travels with the pack.
        WriteConfig("""{ "compatibleMods": ["footprints", "carryon"] }""");

        Assert.Equal(ShareStatus.Pending, _store.ShareStateFor("anego").Status);
    }

    [Fact]
    public void And_the_document_carries_the_value_the_file_has_now()
    {
        Carry("""["footprints"]""");
        WriteConfig("""{ "compatibleMods": ["footprints", "carryon"] }""");

        Assert.Contains("carryon", _store.PublishedDocument("anego", stripConnect: true));
    }

    /// <summary>
    /// And the pack on disk catches up, so the pack somebody opens and the pack somebody
    /// publishes are not two different documents.
    /// </summary>
    [Fact]
    public void Refreshing_writes_the_value_into_the_pack_as_well()
    {
        Carry("""["footprints"]""");
        WriteConfig("""{ "compatibleMods": ["footprints", "carryon"] }""");

        Assert.True(_store.RefreshModConfig("anego"));
        Assert.Contains("carryon",
            _store.Load("anego").ModConfig!["terrainslabs.json"].ToJsonString());

        // And says so only when something moved, or every look at a pack would rewrite it.
        Assert.False(_store.RefreshModConfig("anego"));
    }

    /// <summary>
    /// A key whose file has stopped having it keeps the value the pack declares. Dropping it
    /// would quietly remove it from a shared document over a file somebody may be part-way
    /// through editing; the Mod config tab shows it as an orphan, and unticking is the
    /// decision.
    /// </summary>
    [Fact]
    public void A_setting_whose_file_no_longer_has_it_keeps_what_the_pack_declares()
    {
        Carry("""["footprints"]""");
        WriteConfig("""{ "somethingElse": 1 }""");

        Assert.False(_store.RefreshModConfig("anego"));
        Assert.Contains("footprints", _store.PublishedDocument("anego", stripConnect: true));
    }

    /// <summary>And a pack carrying nothing is not given a section by being looked at.</summary>
    [Fact]
    public void A_pack_that_carries_no_settings_is_left_alone()
    {
        Assert.False(_store.RefreshModConfig("anego"));
        Assert.Null(_store.Load("anego").ModConfig);
    }

    /// <summary>
    /// Refreshing a pack whose files already agree changes nothing at all — not the values,
    /// and not the bytes.
    ///
    /// The manifest is serialised in key order and the document is fingerprinted whole, so a
    /// refresh that produced the same values in a different order moved the fingerprint. That
    /// made every published pack carrying settings report "Publish changes" over a value
    /// nobody had touched, with a summary beside it that could name no difference because
    /// there was none — the pack was right and the button was wrong.
    /// </summary>
    [Fact]
    public void Refreshing_a_pack_that_agrees_with_its_files_changes_not_one_byte()
    {
        // Keys deliberately in a different order in the file from the manifest, which is the
        // ordinary case: the manifest records them as they were ticked.
        WriteConfig("""{ "second": 2, "first": 1, "third": 3 }""");

        var manifest = _store.Load("anego");
        manifest.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] = (JsonNode.Parse(
                """{"first":1,"second":2,"third":3}""") as JsonObject)!,
        };
        _store.Save(manifest);

        var before = _store.PublishedDocument("anego", stripConnect: true);

        Assert.False(_store.RefreshModConfig("anego"));
        Assert.Equal(before, _store.PublishedDocument("anego", stripConnect: true));

        // And the pack agrees it has nothing to publish.
        Publish();
        Assert.Equal(ShareStatus.Shared, _store.ShareStateFor("anego").Status);
    }

    /// <summary>
    /// A value that really did move is replaced where it sits, so the rest of the document is
    /// undisturbed and the difference is the one thing that differs.
    /// </summary>
    [Fact]
    public void A_value_that_moved_is_replaced_in_the_place_it_was_declared()
    {
        WriteConfig("""{ "second": 2, "first": 1, "third": 3 }""");

        var manifest = _store.Load("anego");
        manifest.ModConfig = new Dictionary<string, JsonObject>
        {
            ["terrainslabs.json"] = (JsonNode.Parse(
                """{"first":1,"second":2,"third":3}""") as JsonObject)!,
        };
        _store.Save(manifest);

        WriteConfig("""{ "second": 99, "first": 1, "third": 3 }""");

        Assert.True(_store.RefreshModConfig("anego"));

        Assert.Equal(
            """{"first":1,"second":99,"third":3}""",
            _store.Load("anego").ModConfig!["terrainslabs.json"].ToJsonString());
    }

    // ---- a record left behind by a change to how documents are written ----

    /// <summary>
    /// The publish record keeps a hash of the bytes that were sent, so anything changing how
    /// a document is written moves it: a field that stopped being serialised, a key order
    /// that shifted. The pack then reports having something to publish over a change nobody
    /// made — and publishing to settle it issues a revision identical to its predecessor,
    /// which tells every follower there is an update and then has none for them.
    ///
    /// Told apart from a real change by comparing the whole document, not the summary: a
    /// pack the site is already serving in every field either side carries.
    /// </summary>
    [Fact]
    public void The_same_pack_written_differently_is_not_a_change()
    {
        var mine = PackBundle.Parse(_store.PublishedDocument("anego", stripConnect: true));

        // What the site serves: the same document, plus the envelope it adds. Stripped the
        // same way, since that is what was published.
        var served = (JsonNode.Parse(
            _store.PublishedDocument("anego", stripConnect: true)) as JsonObject)!;

        served["publishedBy"] = "dizzyd";
        served["canonicalUrl"] = "https://cairns.gg/dizzyd/anego";
        served["revision"] = 4;

        Assert.True(PackBundle.Parse(served.ToJsonString()).SameContentAs(mine));
    }

    /// <summary>And a pack that really differs is not mistaken for one that does not.</summary>
    [Fact]
    public void A_pack_that_really_differs_is_still_a_change()
    {
        var mine = PackBundle.Parse(_store.PublishedDocument("anego", stripConnect: true));

        var manifest = _store.Load("anego");
        manifest.Mods.Add(new PackMod { ModId = "carryon" });

        var theirs = PackBundle.Parse(PackBundle.Serialize(manifest));

        Assert.False(theirs.SameContentAs(mine));
    }

    /// <summary>
    /// The same values in a different key order are the same pack. Compared as text this
    /// read as a difference, which is exactly how a document-format change turns into a
    /// revision with nothing in it.
    /// </summary>
    [Fact]
    public void A_key_order_that_moved_is_not_a_change()
    {
        var a = PackBundle.Parse("""
            {"formatVersion":1,"pack":{"id":"anego","gameVersion":"1.22.5","mods":[],
             "modConfig":{"f.json":{"first":1,"second":2}}}}
            """);

        var b = PackBundle.Parse("""
            {"formatVersion":1,"pack":{"gameVersion":"1.22.5","id":"anego","mods":[],
             "modConfig":{"f.json":{"second":2,"first":1}}}}
            """);

        Assert.True(a.SameContentAs(b));
    }

    /// <summary>
    /// A document published by an older Cairn carried a stray "IsPublished", written because
    /// a computed property had no JsonIgnore on it. Dropping it changes the bytes of every
    /// document and so the fingerprint of every published pack — which would have every one
    /// of them reporting "Publish changes" over a field nobody knew existed.
    ///
    /// The content is what settles it: the same pack, said without a field that was never
    /// part of the pack.
    /// </summary>
    [Fact]
    public void A_document_carrying_the_old_stray_field_is_the_same_pack()
    {
        var mine = PackBundle.Parse(_store.PublishedDocument("anego", stripConnect: true));

        var older = (JsonNode.Parse(
            _store.PublishedDocument("anego", stripConnect: true)) as JsonObject)!;

        older["IsPublished"] = false;

        Assert.True(PackBundle.Parse(older.ToJsonString()).SameContentAs(mine));
    }

    /// <summary>And it is not written any more.</summary>
    [Fact]
    public void The_stray_field_is_no_longer_serialised()
    {
        Assert.DoesNotContain("IsPublished", _store.PublishedDocument("anego", stripConnect: true));
        Assert.DoesNotContain("IsPublished", _store.Export("anego"));
    }
}
