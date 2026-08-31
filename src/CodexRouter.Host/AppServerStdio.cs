using System.Text;

namespace CodexRouter.Host;

/// <summary>
/// UTF-8 JSONL streams used by the Router app-server front end.
/// </summary>
public sealed class AppServerStdio : IDisposable
{
    private readonly StreamReader _input;
    private readonly StreamWriter _output;

    private AppServerStdio(StreamReader input, StreamWriter output)
    {
        _input = input;
        _output = output;
    }

    public TextReader Input => _input;
    public TextWriter Output => _output;

    public static AppServerStdio FromConsole()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var input = new StreamReader(
            Console.OpenStandardInput(),
            encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: false);
        var output = new StreamWriter(
            Console.OpenStandardOutput(),
            encoding,
            bufferSize: 16 * 1024,
            leaveOpen: false)
        {
            AutoFlush = false,
            NewLine = "\n"
        };
        return new AppServerStdio(input, output);
    }

    public static AppServerStdio Create(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return new AppServerStdio(
            new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true),
            new StreamWriter(output, encoding, bufferSize: 16 * 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\n" });
    }

    public void Dispose()
    {
        _output.Dispose();
        _input.Dispose();
    }
}
