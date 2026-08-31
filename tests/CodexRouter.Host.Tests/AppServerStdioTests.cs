using System.Text;
using CodexRouter.Host;
using Xunit;

namespace CodexRouter.Host.Tests;

public sealed class AppServerStdioTests
{
    [Fact]
    public async Task App_server_stdio_round_trips_chinese_json_and_cwd_as_utf8_without_bom()
    {
        const string payload = "{\"json\":\"\u4E2D\u6587\",\"cwd\":\"H:\\\\ai\\\\\u5DE5\u4F5C\u533A\\\\\u591A\u8D26\u6237\u5C0F\u63D2\u4EF6\"}";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload + "\n"));
        await using var output = new MemoryStream();
        using var stdio = AppServerStdio.Create(input, output);

        var line = await stdio.Input.ReadLineAsync();
        Assert.Equal(payload, line);

        await stdio.Output.WriteLineAsync(line);
        await stdio.Output.FlushAsync();
        var bytes = output.ToArray();

        Assert.False(bytes.AsSpan().StartsWith(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()));
        Assert.Equal(payload + "\n", Encoding.UTF8.GetString(bytes));
    }
}
