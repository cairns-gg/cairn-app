using Cairn.Core.Packs;
using Xunit;

namespace Cairn.Core.Tests;

public class PackStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Cairn-storetest-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly PackStore _store;

    public PackStoreTests() => _store = new PackStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("anego")]
    [InlineData("vanilla-qol")]
    [InlineData("my_pack_2")]
    [InlineData("A1")]
    public void Valid_ids_are_accepted(string id) => Assert.True(PackStore.IsValidId(id));

    [Theory]
    [InlineData("../evil")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData(null)]
    public void Ids_that_could_escape_the_store_are_rejected(string? id)
    {
        Assert.False(PackStore.IsValidId(id));
        // PackDir is the only place an id becomes a path, so it must refuse too.
        Assert.Throws<ArgumentException>(() => _store.PackDir(id!));
    }

    [Fact]
    public void A_rejected_id_never_creates_anything_on_disk()
    {
        Assert.Throws<ArgumentException>(() => _store.PackDir("../../escaped"));
        Assert.False(Directory.Exists(Path.Combine(_root, "..", "..", "escaped")));
    }

    [Fact]
    public void Create_then_load_round_trips()
    {
        _store.Create("anego", "1.22.5", "Anego Server", "host:42420");

        var loaded = _store.Load("anego");
        Assert.Equal("anego", loaded.Id);
        Assert.Equal("Anego Server", loaded.Name);
        Assert.Equal("1.22.5", loaded.GameVersion);
        Assert.Equal("host:42420", loaded.Connect);
        Assert.True(Directory.Exists(_store.ModsDir("anego")));
    }

    [Fact]
    public void Create_refuses_a_duplicate()
    {
        _store.Create("dup", "1.22.5");
        Assert.Throws<InvalidOperationException>(() => _store.Create("dup", "1.22.5"));
    }

    [Fact]
    public void Create_refuses_an_unusable_game_version()
    {
        Assert.Throws<InvalidDataException>(() => _store.Create("bad", ">=1.22.0"));
        Assert.False(_store.Exists("bad"));
    }

    [Fact]
    public void An_empty_connect_is_stored_as_null_not_empty_string()
    {
        var manifest = _store.Create("sp", "1.22.5", "Single", connect: "   ");
        Assert.Null(manifest.Connect);
        Assert.Null(_store.Load("sp").Connect);
    }

    [Fact]
    public void Delete_removes_the_pack_and_its_mods()
    {
        _store.Create("gone", "1.22.5");
        File.WriteAllText(Path.Combine(_store.ModsDir("gone"), "something_1.0.0.zip"), "x");

        _store.Delete("gone");

        Assert.False(_store.Exists("gone"));
        Assert.False(Directory.Exists(_store.PackDir("gone")));
    }

    [Fact]
    public void ListIds_skips_directories_without_a_manifest()
    {
        _store.Create("real", "1.22.5");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-pack"));

        Assert.Equal(["real"], _store.ListIds());
    }

    [Fact]
    public void DescribeIdProblem_explains_each_rejection()
    {
        Assert.NotNull(_store.DescribeIdProblem(""));
        Assert.NotNull(_store.DescribeIdProblem("../x"));

        _store.Create("taken", "1.22.5");
        Assert.Contains("already exists", _store.DescribeIdProblem("taken")!);

        Assert.Null(_store.DescribeIdProblem("brand-new"));
    }
}
