using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// The classes that move the root by setting an environment variable, run one at a time.
///
/// CAIRN_HOME and CAIRN_DEFAULT_HOME are process-wide, and xunit runs classes in parallel by
/// default — so a class that set CAIRN_HOME for its own sandbox was deciding, for as long as
/// it ran, what <see cref="CairnHome.Resolve"/> answered in every other class. What that
/// looked like was CairnHomeTests and HomeMigrationTests failing a few runs in ten, each time
/// on a different assertion about a pointer file being outranked by an environment variable
/// none of their own code had set. It was misread as a flaky filesystem more than once.
///
/// One collection is the whole fix: within it xunit serialises, so the variable is only ever
/// set by the class currently running. Every class that touches either variable belongs here
/// — one left out reintroduces the race for all of them.
/// </summary>
[CollectionDefinition(Collection)]
public sealed class HomeEnvironment
{
    public const string Collection = "home-environment";
}
