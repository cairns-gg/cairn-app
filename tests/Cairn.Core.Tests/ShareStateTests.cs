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
}
