using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Akka.LogTraceCorrelation;

/// <summary>
/// Message that includes trace context for correlation.
/// </summary>
/// <remarks>
/// In a real implementation, the ActivityContext would be captured in the
/// LogEvent at creation time (before crossing the mailbox boundary).
/// This message demonstrates how trace context flows through the actor system.
/// </remarks>
/// <param name="Content">The message content.</param>
/// <param name="TraceContext">The ActivityContext captured at send time.</param>
public record TracedMessage(string Content, ActivityContext TraceContext);

/// <summary>
/// Example actor that demonstrates trace-correlated logging.
/// </summary>
/// <remarks>
/// <para>
/// This actor receives TracedMessage instances that contain both the message
/// content and the ActivityContext captured at the time the message was sent.
/// </para>
/// <para>
/// When logging, it uses AkkaLogState to pass the trace context through ILogger
/// as structured state. The AkkaTraceContextProcessor then extracts this context
/// and sets it directly on the LogRecord, preserving the exact TraceId and SpanId.
/// </para>
/// </remarks>
public class LoggingActor : ReceiveActor
{
    private readonly ILogger<LoggingActor> _logger;

    /// <summary>
    /// Creates a new LoggingActor with the specified logger.
    /// </summary>
    /// <param name="logger">The ILogger instance to use for logging.</param>
    public LoggingActor(ILogger<LoggingActor> logger)
    {
        _logger = logger;

        Receive<TracedMessage>(HandleTracedMessage);
    }

    private void HandleTracedMessage(TracedMessage msg)
    {
        // Log using custom state that includes correlation IDs from the message
        // The AkkaTraceContextProcessor will extract these and set LogRecord.TraceId/SpanId
        var state = new AkkaLogState(msg.TraceContext, $"Actor received: {msg.Content}");

        _logger.Log(
            MsLogLevel.Information,
            new EventId(0),
            state,
            exception: null,
            formatter: (s, _) => s.ToString());

        Sender.Tell($"Processed: {msg.Content}");
    }
}
