using System.Text.Json;
using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The preferences file, and the bug that made it need a type of its own.
/// </summary>
[Collection(HomeEnvironment.Collection)]
public class CairnSettingsTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "cairn-settings-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly string? _previous = Environment.GetEnvironmentVariable("CAIRN_HOME");

    public CairnSettingsTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("CAIRN_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CAIRN_HOME", _previous);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private string File_ => Path.Combine(_home, "settings.json");

    [Fact]
    public void Nothing_saved_yet_reads_as_the_defaults()
    {
        var settings = CairnSettings.Load();

        Assert.Equal(1.0, settings.UiScale);
        Assert.Null(settings.Language);
    }

    /// <summary>
    /// The whole reason this type exists. UiScale.Save used to serialise a type with one
    /// property on it over the top of the file, so a second setting was erased the first
    /// time somebody dragged the scale slider.
    /// </summary>
    [Fact]
    public void Saving_one_setting_leaves_the_others_alone()
    {
        CairnSettings.Update(s => s.Language = "de");
        CairnSettings.Update(s => s.UiScale = 1.5);

        var settings = CairnSettings.Load();

        Assert.Equal("de", settings.Language);
        Assert.Equal(1.5, settings.UiScale);
    }

    /// <summary>
    /// The same failure one version along: a newer Cairn writes a setting, somebody goes
    /// back to an older build, and the older one erases what it does not recognise.
    /// </summary>
    [Fact]
    public void A_setting_this_build_does_not_know_survives_a_write()
    {
        System.IO.File.WriteAllText(File_, """
        { "UiScale": 1.25, "somethingNewer": { "on": true } }
        """);

        CairnSettings.Update(s => s.UiScale = 2.0);

        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(File_));

        Assert.Equal(2.0, document.RootElement.GetProperty("UiScale").GetDouble());
        Assert.True(document.RootElement.GetProperty("somethingNewer").GetProperty("on").GetBoolean());
    }

    /// <summary>
    /// The property name is the key in a file people already have. Renaming it would read
    /// as "no scale saved" and quietly put everybody back to 100%.
    /// </summary>
    [Fact]
    public void The_scale_keeps_the_name_it_was_written_under()
    {
        System.IO.File.WriteAllText(File_, """{ "UiScale": 1.75 }""");

        Assert.Equal(1.75, CairnSettings.Load().UiScale);
    }

    [Fact]
    public void An_unreadable_file_costs_the_defaults_and_not_a_start_up()
    {
        System.IO.File.WriteAllText(File_, "{ this is not json");

        Assert.Equal(1.0, CairnSettings.Load().UiScale);
    }

    /// <summary>
    /// Null rather than the resolved answer: storing "en" the first time somebody opened
    /// Preferences would freeze the launcher in whatever it happened to start in, and stop
    /// it following the game.
    /// </summary>
    [Fact]
    public void Automatic_is_stored_as_nothing_at_all()
    {
        CairnSettings.Update(s => s.Language = "de");
        CairnSettings.Update(s => s.Language = null);

        Assert.Null(CairnSettings.Load().Language);
        Assert.DoesNotContain("\"Language\"", System.IO.File.ReadAllText(File_));
    }

    [Fact]
    public void A_saved_language_is_what_the_resolver_uses()
    {
        CairnSettings.Update(s => s.Language = "de");

        var (code, source) = LanguageChoice.Resolve(CairnSettings.Load().Language);

        Assert.Equal("de", code);
        Assert.Equal(LanguageSource.Chosen, source);
    }
}
