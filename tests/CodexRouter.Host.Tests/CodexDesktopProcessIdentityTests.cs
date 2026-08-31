using CodexRouter.Host;
using Xunit;

namespace CodexRouter.Host.Tests;

public sealed class CodexDesktopProcessIdentityTests
{
    [Theory]
    [InlineData("Codex", null, null, true)]
    [InlineData("codex", null, null, true)]
    [InlineData("ChatGPT", "OpenAI.Codex_2p2nqsd0c76g0", null, true)]
    [InlineData("ChatGPT", null, "C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.810.7004.0_x64__2p2nqsd0c76g0\\app\\ChatGPT.exe", true)]
    [InlineData("ChatGPT", "OpenAI.ChatGPT_123", "C:\\Program Files\\WindowsApps\\OpenAI.ChatGPT_1_x64__123\\app\\ChatGPT.exe", false)]
    [InlineData("ChatGPT", null, null, false)]
    [InlineData("chrome", "OpenAI.Codex_2p2nqsd0c76g0", "C:\\Program Files\\WindowsApps\\OpenAI.Codex_1\\app\\ChatGPT.exe", false)]
    public void Matches_only_codex_desktop_identities(
        string processName,
        string? packageFamilyName,
        string? executablePath,
        bool expected)
    {
        Assert.Equal(expected, CodexDesktopProcessIdentity.Matches(processName, packageFamilyName, executablePath));
    }
}
