namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Serilog's SelfLog, with the Seq sink's connection failures cut to one line.
/// </summary>
/// <remarks>
/// <para>
/// Serilog swallows sink exceptions by design, so a Seq that
/// has moved and a log folder the process cannot write both look like a quiet day. SelfLog is
/// the only thing that says otherwise, so enable it first thing in the process:
/// </para>
/// <code>
/// SelfLog.Enable(new SelfLogOutput(Console.Error, "See the logging runbook.").Write);
/// </code>
/// <para>
/// Seq is optional in these applications: they run without it and the other sinks keep every
/// event. Plain SelfLog printed a twenty-line stack trace for every retry while Seq was
/// stopped, about once a minute for as long as the process ran. Only an unreachable Seq is cut
/// down. A Seq that answers and refuses the events, or any other sink failing, is a fault and
/// passes through whole.
/// </para>
/// <para>
/// <strong>Known gap: the one line is written once per process.</strong> A Seq that comes
/// back and then stops again is not reported a second time, and nothing says when it came
/// back.
/// </para>
/// <para>
/// It matches the Seq sink by the text of its stack trace and takes no dependency on
/// Serilog.Sinks.Seq. If that sink renames its namespace, connection failures pass through
/// whole again, which is loud and safe.
/// </para>
/// </remarks>
/// <param name="output">Where to write, usually <see cref="Console.Error"/>.</param>
/// <param name="seeAlso">
/// A closing sentence for the one line, such as <c>See the logging runbook.</c> Null or blank
/// adds nothing.
/// </param>
/// <param name="prefix">
/// Text put in front of every message this writes, the one line included, followed by a
/// space. It goes on the first line of a message only, so a stack trace keeps its shape. Use
/// it so SelfLog output can be found in deploy or service output by one search, such as
/// <c>Serilog self log:</c>. Null or blank adds nothing, which is the default.
/// </param>
public sealed class SelfLogOutput(TextWriter output, string? seeAlso = null, string? prefix = null)
{
    private const string ConnectionFailure = "System.Net.Http.HttpRequestException: ";
    private const string SeqSink = "Serilog.Sinks.Seq";

    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly string _seeAlso = string.IsNullOrWhiteSpace(seeAlso) ? string.Empty : " " + seeAlso.Trim();
    private readonly string _prefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim() + " ";

    private int _seqUnreachableReported;

    /// <summary>
    /// Writes one SelfLog message. Hand this method to <c>SelfLog.Enable</c>.
    /// </summary>
    /// <param name="message">The message as SelfLog formatted it, timestamp first.</param>
    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var cause = SeqConnectionFailure(message);
        if (cause is null)
        {
            _output.WriteLine(_prefix + message);
            return;
        }

        // Once per process, so a Seq that comes back and stops again is not reported twice.
        if (Interlocked.Exchange(ref _seqUnreachableReported, 1) == 0)
        {
            _output.WriteLine(
                $"{_prefix}{message.Split(' ', 2)[0]} Seq is not reachable ({cause}). The application runs "
                + "normally and its other sinks still log. Further Seq connection failures are "
                + $"not shown.{_seeAlso}");
        }
    }

    // The batching sink names only itself in the first line, so the Seq sink shows up in the
    // stack trace. The HttpRequestException has to be the outer exception: a line starting
    // " ---> " is an inner one, and whatever wrapped it is worth seeing whole.
    private static string? SeqConnectionFailure(string message)
    {
        if (!message.Contains(SeqSink, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var line in message.Split('\n'))
        {
            if (line.StartsWith(ConnectionFailure, StringComparison.Ordinal))
            {
                return line[ConnectionFailure.Length..].TrimEnd('\r');
            }
        }

        return null;
    }
}
