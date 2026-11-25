using System.Net;
using Serilog;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Ported with the code.
/// </summary>
public class TransportExceptionLogTests
{
    private static HttpRequestException Thrown(HttpStatusCode? status)
    {
        try
        {
            throw new HttpRequestException("customer Private Name at https://remote.test/123456789", null, status);
        }
        catch (HttpRequestException caught)
        {
            return caught;
        }
    }

    [Fact]
    public void Transport_failure_logs_keep_metadata_without_private_response_text()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var original = Thrown(HttpStatusCode.BadRequest);
        var wrapper = new InvalidOperationException("Private outer response detail", original);

        logger.Error(TransportExceptionLog.Snapshot(wrapper), "{Event} request failed", "http.request.failed");

        var written = Assert.Single(sink.Events);
        Assert.NotNull(written.Exception);
        Assert.Contains("status 400", written.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", written.Exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", written.Exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", written.Exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("remote.test", written.Exception.ToString(), StringComparison.Ordinal);
        Assert.Null(written.Exception.InnerException);
        Assert.Contains(nameof(Thrown), written.Exception.StackTrace, StringComparison.Ordinal);
        Assert.Contains("Private Name", original.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_that_got_no_response_says_the_status_is_unavailable()
    {
        var snapshot = TransportExceptionLog.Snapshot(Thrown(status: null));

        Assert.Equal(
            "System.Net.Http.HttpRequestException: outbound HTTP request failed; status unavailable. "
            + "Response text and request URLs are omitted from logs.",
            snapshot!.Message);
    }

    [Fact]
    public void A_failure_inside_an_aggregate_is_found()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("Local"),
            new InvalidOperationException("Private wrapper", Thrown(HttpStatusCode.BadGateway)));

        Assert.True(TransportExceptionLog.IsTransportFailure(aggregate));
        Assert.Contains("status 502", TransportExceptionLog.Snapshot(aggregate)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nontransport_exceptions_keep_the_original_diagnostics()
    {
        var exception = new InvalidOperationException("Local invariant failed");

        Assert.Same(exception, TransportExceptionLog.Snapshot(exception));
        Assert.False(TransportExceptionLog.IsTransportFailure(exception));
    }

    [Fact]
    public void No_exception_is_no_snapshot()
    {
        Assert.Null(TransportExceptionLog.Snapshot(null));
        Assert.False(TransportExceptionLog.IsTransportFailure(null));
    }
}
