namespace ModularRuntime.Ipc.Tests;

public sealed class DetachedProcessTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\App", "\"C:\\Program Files\\App\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData(@"C:\no\space\", @"C:\no\space\")]
    public void QuoteArgument_FollowsCommandLineToArgvRules(string argument, string expected)
    {
        Assert.Equal(expected, DetachedProcess.QuoteArgument(argument));
    }
}
