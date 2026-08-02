using Cairn.Core;
using Xunit;

namespace Cairn.Core.Tests;

/// <summary>
/// What the app reports as its version.
/// </summary>
public class CairnVersionTests
{
    [Fact]
    public void An_unstamped_build_says_dev_rather_than_inventing_a_number()
    {
        // The test host is not stamped, so this is the unstamped case. "1.0.0" is what the
        // SDK supplies when nobody said otherwise, and reporting it would be a claim that
        // this is a release — which is the sort of claim people act on.
        Assert.Equal("dev", CairnVersion.Current);
    }
}
