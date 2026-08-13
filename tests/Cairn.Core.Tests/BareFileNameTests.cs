using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The rule three subsystems lean on before combining a remote string with a directory.
/// Each case here is a name that arrived from somebody else's JSON in at least one of
/// them: a mod's lock entry, the game version manifest, or Microsoft's release index.
/// </summary>
public class BareFileNameTests
{
    [Theory]
    [InlineData("carryon_2.6.1.zip")]
    [InlineData("dotnet-runtime-10.0.0-win-x64.zip")]
    [InlineData("vs_install_win-x64_1.22.6.exe")]
    [InlineData("a")]
    [InlineData("mod.name.with.dots.zip")]
    [InlineData("Ünïcödé mod.zip")]
    public void Accepts_an_ordinary_file_name(string name) =>
        Assert.True(BareFileName.IsBare(name));

    [Theory]
    [InlineData("../evil.zip")]
    [InlineData("..\\evil.zip")]
    [InlineData("a/b.zip")]
    [InlineData("a\\b.zip")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.exe")]
    [InlineData("\\\\server\\share\\evil.exe")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_anything_carrying_a_path(string? name) =>
        Assert.False(BareFileName.IsBare(name));

    /// <summary>
    /// The colon survives Path.GetFileName untouched, so it has to be named separately —
    /// on Windows "mod.zip:hidden" is an alternate data stream that File.Create writes
    /// happily and no directory listing shows.
    /// </summary>
    [Theory]
    [InlineData("mod.zip:hidden")]
    [InlineData("mod.zip:$DATA")]
    public void Rejects_an_alternate_data_stream(string name) =>
        Assert.False(BareFileName.IsBare(name));

    /// <summary>
    /// Win32 resolves a reserved device name ahead of the filesystem whatever extension
    /// follows it, so File.Create on "COM1.zip" opens the serial port.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.dll")]
    [InlineData("COM1.zip")]
    [InlineData("com1.zip")]
    [InlineData("LPT9.cs")]
    [InlineData("AUX.zip")]
    [InlineData("PRN")]
    [InlineData("COM1 .zip")]
    public void Rejects_a_reserved_device_name(string name) =>
        Assert.False(BareFileName.IsBare(name));

    /// <summary>
    /// Only the exact device names. "CONFIG.zip" and "COM10.zip" are ordinary files, and
    /// refusing them would reject real mods for looking slightly like a hazard.
    /// </summary>
    [Theory]
    [InlineData("CONFIG.zip")]
    [InlineData("COM10.zip")]
    [InlineData("CONSOLE.dll")]
    [InlineData("NULL.zip")]
    [InlineData("com.example.mod.zip")]
    public void Accepts_a_name_that_merely_starts_like_one(string name) =>
        Assert.True(BareFileName.IsBare(name));
}
