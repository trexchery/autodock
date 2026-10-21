using Handoff;
using Xunit;

namespace Handoff.Tests;

public class GeneralAgentTests
{
    [Fact]
    public void FolderIsFixedUnderTheProfile() =>
        Assert.Equal("handoff-general", Path.GetFileName(Launch.GeneralFolder));

    [Fact]
    public void RunsOnTheDefaultLoginNotPinnedToAnAccount() => // `cswap run` with no account in an unmapped folder: the default login
        Assert.Equal(["run", "--restore-rc"], Launch.Run(null, restoreRc: true));
}
