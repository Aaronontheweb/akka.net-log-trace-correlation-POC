using System.Collections;
using System.Diagnostics;

namespace Akka.LogTraceCorrelation;

/// <summary>
/// Custom log state that captures ActivityContext at construction time.
/// This allows the LogRecordProcessor to extract the correlation IDs
/// and set them directly on the LogRecord without creating child spans.
/// </summary>
/// <remarks>
/// Implements IReadOnlyList so OpenTelemetry can parse it with ParseStateValues=true.
/// The trace context is passed as structured data that the AkkaTraceContextProcessor
/// extracts and uses to set LogRecord.TraceId and LogRecord.SpanId directly.
/// </remarks>
public readonly struct AkkaLogState : IReadOnlyList<KeyValuePair<string, object?>>
{
    /// <summary>
    /// Key used to store TraceId in log attributes.
    /// </summary>
    public const string TraceIdKey = "Akka.TraceId";

    /// <summary>
    /// Key used to store SpanId in log attributes.
    /// </summary>
    public const string SpanIdKey = "Akka.SpanId";

    /// <summary>
    /// Key used to store TraceFlags in log attributes.
    /// </summary>
    public const string TraceFlagsKey = "Akka.TraceFlags";

    private readonly KeyValuePair<string, object?>[] _items;

    /// <summary>
    /// Creates a new AkkaLogState with the given ActivityContext and message.
    /// </summary>
    /// <param name="context">The ActivityContext to capture (TraceId, SpanId, TraceFlags).</param>
    /// <param name="message">The log message.</param>
    public AkkaLogState(ActivityContext context, string message)
    {
        _items =
        [
            new KeyValuePair<string, object?>(TraceIdKey, context.TraceId.ToString()),
            new KeyValuePair<string, object?>(SpanIdKey, context.SpanId.ToString()),
            new KeyValuePair<string, object?>(TraceFlagsKey, context.TraceFlags.ToString()),
            new KeyValuePair<string, object?>("{OriginalFormat}", message)
        ];
    }

    /// <summary>
    /// Creates a new AkkaLogState with explicit TraceId, SpanId, and TraceFlags.
    /// </summary>
    public AkkaLogState(ActivityTraceId traceId, ActivitySpanId spanId, ActivityTraceFlags traceFlags, string message)
    {
        _items =
        [
            new KeyValuePair<string, object?>(TraceIdKey, traceId.ToString()),
            new KeyValuePair<string, object?>(SpanIdKey, spanId.ToString()),
            new KeyValuePair<string, object?>(TraceFlagsKey, traceFlags.ToString()),
            new KeyValuePair<string, object?>("{OriginalFormat}", message)
        ];
    }

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index] => _items[index];

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)_items).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    public override string ToString() =>
        _items.FirstOrDefault(x => x.Key == "{OriginalFormat}").Value?.ToString() ?? string.Empty;
}
