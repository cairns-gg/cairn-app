namespace Cairn.App.ViewModels;

/// <summary>
/// A yes/no the user has to look at.
///
/// Exists because an inline confirmation is only safe when it is certain to be on screen.
/// The pack delete prompt sat at the bottom of a scrolling tab, so arming it rendered the
/// warning below the fold — the one place a destructive prompt must never be.
/// </summary>
public sealed class ConfirmViewModel(string title, string message, string confirmLabel)
{
    public string Title { get; } = title;

    /// <summary>What will happen. Stated in full: this is the last thing read before yes.</summary>
    public string Message { get; } = message;

    /// <summary>Names the action rather than saying "OK", so the button itself is the warning.</summary>
    public string ConfirmLabel { get; } = confirmLabel;
}
