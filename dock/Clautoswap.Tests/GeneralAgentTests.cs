using Clautoswap;
using Xunit;

namespace Clautoswap.Tests;

public class GeneralAgentTests
{
    static Account A(int n, double five, bool active = false, bool disabled = false, string provider = "claude") =>
        new(n, $"{n}@example.com", active, "ok", new Usage(new Window(five, null), new Window(10, null), null, null), Disabled: disabled) { Provider = provider };

    [Fact]
    public void ActiveClaudeAccountWins() =>
        Assert.Equal(2, Launch.GeneralAccount([A(1, 0), A(2, 90, active: true)]));

    [Fact]
    public void NoActiveClaudeAccountTakesTheMostRoom() =>
        Assert.Equal(3, Launch.GeneralAccount([A(1, 60), A(2, 5, disabled: true), A(3, 20), A(4, 0, provider: "codex")]));

    [Fact]
    public void UnreadableUsageIsNeverTheMostRoom() =>
        Assert.Equal(1, Launch.GeneralAccount([A(1, 40), new Account(2, "2@example.com", false, "error", null)]));

    [Fact]
    public void OnlyUnreadableUsageLeavesItToTheEngine() =>
        Assert.Null(Launch.GeneralAccount([new Account(2, "2@example.com", false, "expired", null)]));

    [Fact]
    public void NoClaudeAccountLeavesItToTheEngine() =>
        Assert.Null(Launch.GeneralAccount([A(4, 0, provider: "codex")]));

    [Fact]
    public void FolderIsFixedUnderTheProfile() =>
        Assert.Equal("clautoswap-general", Path.GetFileName(Launch.GeneralFolder));
}
