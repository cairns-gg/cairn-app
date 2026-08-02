using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// Saying which program this is.
/// </summary>
[Collection(AvaloniaTests.Collection)]
public class AboutTests
{
    [AvaloniaFact]
    public void The_application_menu_names_Cairn_and_not_the_toolkit()
    {
        var menu = NativeMenu.GetMenu(Application.Current!);

        // Without a menu of our own, Avalonia supplies one whose About item reads "About
        // Avalonia" — the name of the toolkit, in the one menu meant to carry the name of
        // the program. Only macOS draws this, so what is checked here is that we supplied
        // it and what it says.
        Assert.NotNull(menu);

        var about = menu!.Items.OfType<NativeMenuItem>().FirstOrDefault();

        Assert.NotNull(about);
        Assert.Equal("About Cairn", about!.Header);
        Assert.True(about.HasClickHandlers);
    }
}
