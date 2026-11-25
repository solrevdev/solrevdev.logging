namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// A stopped Seq is normal in these applications and must say so once. Every other SelfLog
/// message is a sink fault and must still arrive whole, or the filter has brought back the
/// silence SelfLog exists to break.
/// </summary>
/// <remarks>
/// The messages are what SelfLog printed in a running application with Seq stopped, stack
/// traces cut short. They are fixtures and not a live Seq sink because
/// SelfLog is process-wide.
/// </remarks>
public class SelfLogOutputTests
{
    private const string SeqRefusedConnection =
        "2026-09-15T08:48:51.1212480Z Serilog.Core.Sinks.Batching.BatchingSink: failed emitting a batch (Temporary, 1 events)\n"
        + "System.Net.Http.HttpRequestException: Connection refused (localhost:5341)\n"
        + " ---> System.Net.Sockets.SocketException (61): Connection refused\n"
        + "   at System.Net.Sockets.Socket.AwaitableSocketAsyncEventArgs.ThrowException(SocketError error, CancellationToken cancellationToken)\n"
        + "   --- End of inner exception stack trace ---\n"
        + "   at Serilog.Sinks.Seq.Http.SeqIngestionApiClient.TryIngestAsync(String payload, String mediaType)\n"
        + "   at Serilog.Sinks.Seq.Batched.BatchedSeqSink.EmitBatchAsync(IReadOnlyCollection`1 events)\n"
        + "   at Serilog.Core.Sinks.Batching.BatchingSink.LoopAsync()";

    [Fact]
    public void A_stopped_seq_is_reported_once_in_one_line()
    {
        var output = new StringWriter();
        var selfLog = new SelfLogOutput(output);

        selfLog.Write(SeqRefusedConnection);
        selfLog.Write(SeqRefusedConnection.Replace("1 events", "9 events", StringComparison.Ordinal));

        var line = Assert.Single(Lines(output));
        Assert.Equal(
            "2026-09-15T08:48:51.1212480Z Seq is not reachable (Connection refused (localhost:5341)). "
            + "The application runs normally and its other sinks still log. "
            + "Further Seq connection failures are not shown.",
            line);
    }

    [Fact]
    public void The_closing_sentence_is_the_applications_to_give()
    {
        var output = new StringWriter();

        new SelfLogOutput(output, "See the logging runbook.").Write(SeqRefusedConnection);

        Assert.EndsWith(
            "Further Seq connection failures are not shown. See the logging runbook.",
            Assert.Single(Lines(output)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_closing_sentence_adds_nothing(string? seeAlso)
    {
        var output = new StringWriter();

        new SelfLogOutput(output, seeAlso).Write(SeqRefusedConnection);

        Assert.EndsWith("are not shown.", Assert.Single(Lines(output)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_seq_that_answers_and_refuses_the_events_is_shown_in_full()
    {
        // A wrong API key or an oversized batch: Seq is running and the events are being lost.
        var message =
            "2026-09-15T08:48:55.4125620Z Serilog.Core.Sinks.Batching.BatchingSink: failed emitting a batch (Temporary, 9 events)\n"
            + "Serilog.Debugging.LoggingFailedException: Received failed result Unauthorized when posting events to Seq.\n"
            + "   at Serilog.Sinks.Seq.Batched.BatchedSeqSink.EmitBatchAsync(IReadOnlyCollection`1 events)\n"
            + "   at Serilog.Core.Sinks.Batching.BatchingSink.LoopAsync()";

        AssertPassedThrough(message);
    }

    [Fact]
    public void A_connection_failure_wrapped_in_something_else_is_shown_in_full()
    {
        // The HttpRequestException has to be the outer exception. Whatever wrapped it is worth
        // seeing whole.
        var message =
            "2026-09-15T08:48:55.4125620Z Serilog.Core.Sinks.Batching.BatchingSink: failed emitting a batch (Temporary, 9 events)\n"
            + "System.InvalidOperationException: The sink gave up\n"
            + " ---> System.Net.Http.HttpRequestException: Connection refused (localhost:5341)\n"
            + "   at Serilog.Sinks.Seq.Batched.BatchedSeqSink.EmitBatchAsync(IReadOnlyCollection`1 events)";

        AssertPassedThrough(message);
    }

    [Fact]
    public void Another_sink_failing_is_shown_in_full()
    {
        AssertPassedThrough(
            "2026-09-15T08:48:51.4321810Z Caught exception while emitting to sink Serilog.Sinks.File.FileSink: "
            + "System.UnauthorizedAccessException: Access to the path '/app/logs/log-20260915.txt' is denied.\n"
            + "   at Serilog.Sinks.File.FileSink.Emit(LogEvent logEvent)");
    }

    [Fact]
    public void A_sink_failure_after_a_stopped_seq_is_still_shown()
    {
        var output = new StringWriter();
        var selfLog = new SelfLogOutput(output);
        const string fileSinkFailure = "2026-09-15T08:49:00.0000000Z Caught exception while emitting to sink Serilog.Sinks.File.FileSink: System.IO.IOException: No space left on device";

        selfLog.Write(SeqRefusedConnection);
        selfLog.Write(fileSinkFailure);

        Assert.Equal(fileSinkFailure, Lines(output)[1]);
    }

    [Fact]
    public void The_known_gap_a_seq_that_comes_back_and_stops_again_is_not_reported_twice()
    {
        // Pinned so the gap is a stated behaviour. Nothing tells this class Seq came back, so
        // it cannot know the second failure is a new outage.
        var output = new StringWriter();
        var selfLog = new SelfLogOutput(output);

        selfLog.Write(SeqRefusedConnection);
        selfLog.Write(SeqRefusedConnection.Replace("08:48:51", "11:30:00", StringComparison.Ordinal));

        Assert.Single(Lines(output));
    }

    [Fact]
    public void A_prefix_goes_on_the_first_line_of_everything_written()
    {
        // So one search of deploy output finds every SelfLog message, the Seq line included.
        var output = new StringWriter();
        var selfLog = new SelfLogOutput(output, prefix: " Serilog self log: ");
        const string fileSinkFailure =
            "2026-09-15T08:49:00.0000000Z Caught exception while emitting to sink Serilog.Sinks.File.FileSink: System.IO.IOException: No space left on device\n"
            + "   at Serilog.Sinks.File.FileSink.Emit(LogEvent logEvent)";

        selfLog.Write(SeqRefusedConnection);
        selfLog.Write(fileSinkFailure);

        var lines = Lines(output);
        Assert.StartsWith("Serilog self log: 2026-09-15T08:48:51.1212480Z Seq is not reachable (", lines[0], StringComparison.Ordinal);
        Assert.Equal("Serilog self log: " + fileSinkFailure + Environment.NewLine, output.ToString()[(lines[0].Length + Environment.NewLine.Length)..]);

        // The stack trace line is as SelfLog wrote it.
        Assert.Contains("\n   at Serilog.Sinks.File.FileSink.Emit", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_prefix_is_the_default_and_a_blank_one_adds_nothing(string? prefix)
    {
        const string message = "2026-09-15T08:49:00.0000000Z Caught exception while emitting to sink Serilog.Sinks.File.FileSink: System.IO.IOException: No space left on device";
        var output = new StringWriter();

        new SelfLogOutput(output, seeAlso: null, prefix: prefix).Write(message);

        Assert.Equal(message + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new SelfLogOutput(null!));
        Assert.Throws<ArgumentNullException>(() => new SelfLogOutput(new StringWriter()).Write(null!));
    }

    private static void AssertPassedThrough(string message)
    {
        var output = new StringWriter();

        new SelfLogOutput(output).Write(message);

        Assert.Equal(message + Environment.NewLine, output.ToString());
    }

    private static string[] Lines(StringWriter output) =>
        output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
}
