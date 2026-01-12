using System.Diagnostics;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Akka.LogTraceCorrelation;

/// <summary>
/// LogRecordProcessor that extracts TraceId/SpanId from log attributes
/// and sets them directly on the LogRecord without creating child spans.
/// </summary>
/// <remarks>
/// <para>
/// This processor solves the problem of Activity.Current not flowing across
/// actor mailbox boundaries. Instead of trying to restore Activity.Current
/// (which would create child spans), we capture the ActivityContext at log
/// creation time, pass it through ILogger as structured state, and then
/// extract it here to set LogRecord.TraceId/SpanId directly.
/// </para>
/// <para>
/// IMPORTANT: This processor must be registered BEFORE exporters in the
/// OpenTelemetry logging pipeline so that TraceId/SpanId are set before export.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// builder.Logging.AddOpenTelemetry(options =>
/// {
///     // Register processor FIRST
///     options.AddProcessor(new AkkaTraceContextProcessor());
///     options.ParseStateValues = true; // Required to parse AkkaLogState
///     options.AddOtlpExporter();
/// });
/// </code>
/// </example>
public sealed class AkkaTraceContextProcessor : BaseProcessor<LogRecord>
{
    /// <inheritdoc />
    public override void OnEnd(LogRecord logRecord)
    {
        // Skip if no attributes to process
        if (logRecord.Attributes == null)
        {
            base.OnEnd(logRecord);
            return;
        }

        // Skip if LogRecord already has trace context (from Activity.Current)
        if (logRecord.TraceId != default)
        {
            base.OnEnd(logRecord);
            return;
        }

        string? traceIdStr = null;
        string? spanIdStr = null;
        ActivityTraceFlags traceFlags = default;

        // Extract correlation IDs from attributes
        foreach (var attribute in logRecord.Attributes)
        {
            switch (attribute.Key)
            {
                case AkkaLogState.TraceIdKey:
                    traceIdStr = attribute.Value as string;
                    break;

                case AkkaLogState.SpanIdKey:
                    spanIdStr = attribute.Value as string;
                    break;

                case AkkaLogState.TraceFlagsKey:
                    if (attribute.Value is string traceFlagsStr &&
                        Enum.TryParse<ActivityTraceFlags>(traceFlagsStr, out var parsedFlags))
                    {
                        traceFlags = parsedFlags;
                    }
                    break;
            }
        }

        // Parse and set the correlation IDs directly on the LogRecord
        // This is the key insight: we're NOT creating an Activity,
        // we're setting the LogRecord fields directly to preserve
        // the EXACT original TraceId and SpanId (no child spans!)
        if (traceIdStr is { Length: 32 } && spanIdStr is { Length: 16 })
        {
            if (TryParseTraceId(traceIdStr, out var traceId) &&
                TryParseSpanId(spanIdStr, out var spanId))
            {
                logRecord.TraceId = traceId;
                logRecord.SpanId = spanId;
                logRecord.TraceFlags = traceFlags;
            }
        }

        base.OnEnd(logRecord);
    }

    private static bool TryParseTraceId(string hexString, out ActivityTraceId traceId)
    {
        traceId = default;
        if (hexString.Length != 32)
            return false;

        Span<byte> bytes = stackalloc byte[16];
        if (!TryParseHexString(hexString, bytes))
            return false;

        traceId = ActivityTraceId.CreateFromBytes(bytes);
        return true;
    }

    private static bool TryParseSpanId(string hexString, out ActivitySpanId spanId)
    {
        spanId = default;
        if (hexString.Length != 16)
            return false;

        Span<byte> bytes = stackalloc byte[8];
        if (!TryParseHexString(hexString, bytes))
            return false;

        spanId = ActivitySpanId.CreateFromBytes(bytes);
        return true;
    }

    private static bool TryParseHexString(string hexString, Span<byte> bytes)
    {
        if (hexString.Length != bytes.Length * 2)
            return false;

        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hexString.AsSpan(i * 2, 2), NumberStyles.HexNumber, null, out bytes[i]))
                return false;
        }

        return true;
    }
}
