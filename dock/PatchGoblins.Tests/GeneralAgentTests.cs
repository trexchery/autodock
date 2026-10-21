using PatchGoblins;
using Xunit;

namespace PatchGoblins.Tests;

public class GeneralAgentTests
{
    [Fact]
    public void FolderIsFixedUnderTheProfile() =>
        Assert.Equal(Directory.Exists(Path.Combine(StoreSecurity.UserProfile, "handoff-general")) ? "handoff-general" : "patch-goblins-general", Path.GetFileName(Launch.GeneralFolder)); // an existing folder keeps its name

    [Fact]
    public void RunsOnTheDefaultLoginNotPinnedToAnAccount() => // `cswap run` with no account in an unmapped folder: the default login
        Assert.Equal(["run", "--restore-rc"], Launch.Run(null, restoreRc: true));
}
