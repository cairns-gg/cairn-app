namespace Cairn.Core.Tests;

/// <summary>
/// Progress delivered where it is reported, rather than posted to be delivered later.
///
/// <see cref="Progress{T}"/> captures the current synchronisation context, and a test has
/// none — so it posts every callback to the thread pool instead of running it. A test that
/// awaits the work and then asserts on what it was told is asserting about whatever
/// happened to have been delivered by the time the await returned, which is not the same
/// thing and is not what anybody writing the test meant.
///
/// It usually passes. It fails when the machine is busy, which is to say in CI, or on
/// whoever adds the next slow test beside it — one run in fifty, reported against a test
/// that is not the one at fault, with a number that looks like a real bug: a copy that
/// stopped at 3 MB of 5, an import that planned eleven of twelve mods.
///
/// Production is not affected and must keep using <see cref="Progress{T}"/>: the whole
/// point of it there is that a view model's callback arrives on the UI thread. This is for
/// tests, where the sink is a list and the assertion is the next line.
///
/// Callbacks arrive on whichever thread reported them, so a sink written to from a
/// background reader still needs its own lock.
/// </summary>
internal sealed class Reports<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
