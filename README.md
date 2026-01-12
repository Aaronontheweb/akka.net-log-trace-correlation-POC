# Akka.NET Log/Trace Correlation Proof of Concept

This repository demonstrates how to correlate Akka.NET actor logs with OpenTelemetry traces, solving the problem that `Activity.Current` doesn't flow across actor mailbox boundaries.

**Related Issue**: [akkadotnet/akka.net#6855](https://github.com/akkadotnet/akka.net/issues/6855)

## The Problem

When using Akka.NET with OpenTelemetry, logs generated inside actors don't correlate with the parent trace because:

1. `Activity.Current` uses `AsyncLocal<T>` for context propagation
2. Actor mailboxes schedule message processing on thread pool threads
3. `AsyncLocal<T>` doesn't flow across the `Tell()` boundary
4. Result: `Activity.Current` is `null` when the actor processes the message

## The Solution

This PoC demonstrates the **LogRecordProcessor approach**:

1. **Capture** `ActivityContext` at message send time (before mailbox crossing)
2. **Pass** it through `ILogger` as structured state (`AkkaLogState`)
3. **Extract** it in `AkkaTraceContextProcessor` and set `LogRecord.TraceId/SpanId` directly

**Key insight**: We don't create `Activity` objects (which would generate child spans). We set `LogRecord.TraceId` and `LogRecord.SpanId` directly, preserving the **exact original TraceId AND SpanId**.

## Why Not Use `Activity.SetParentId()`?

A previous approach tried creating a temporary `Activity` with `SetParentId()`:

```csharp
// DON'T DO THIS - Creates child spans!
var tempActivity = new Activity("Context");
tempActivity.SetParentId(ctx.TraceId, ctx.SpanId, ctx.TraceFlags);
tempActivity.Start();
// tempActivity.SpanId is a NEW RANDOM ID, not ctx.SpanId!
```

This creates **child spans** with new SpanIds, defeating trace correlation. Multiple logs would have different SpanIds.

## Key Components

### AkkaLogState

A struct implementing `IReadOnlyList<KeyValuePair<string, object?>>` that captures trace context:

```csharp
var state = new AkkaLogState(activityContext, "Log message");
logger.Log(LogLevel.Information, new EventId(), state, null, (s, _) => s.ToString());
```

### AkkaTraceContextProcessor

A `BaseProcessor<LogRecord>` that extracts trace context from log attributes and sets it directly:

```csharp
public override void OnEnd(LogRecord logRecord)
{
    // Extract from attributes
    var traceId = ExtractTraceId(logRecord.Attributes);
    var spanId = ExtractSpanId(logRecord.Attributes);

    // Set DIRECTLY - no Activity creation!
    logRecord.TraceId = traceId;
    logRecord.SpanId = spanId;
}
```

## Usage

### Configuration

```csharp
builder.Logging.AddOpenTelemetry(options =>
{
    // Register processor FIRST (before exporters)
    options.AddProcessor(new AkkaTraceContextProcessor());

    // Required to parse AkkaLogState
    options.ParseStateValues = true;

    // Add your exporter
    options.AddOtlpExporter();
});
```

### Logging with Trace Context

```csharp
// In your actor
public class MyActor : ReceiveActor
{
    private readonly ILogger _logger;

    public MyActor(ILogger<MyActor> logger)
    {
        _logger = logger;

        Receive<TracedMessage>(msg =>
        {
            var state = new AkkaLogState(msg.TraceContext, $"Processing {msg.Content}");
            _logger.Log(LogLevel.Information, new EventId(), state, null, (s, _) => s.ToString());
        });
    }
}
```

## Running the Demo

```bash
dotnet run --project src/Akka.LogTraceCorrelation
```

Expected output shows all logs have the **same TraceId AND SpanId** as the original Activity:

```
╔════════════════════════════════════════════════════════════════╗
║  Akka.NET Log/Trace Correlation Proof of Concept               ║
╚════════════════════════════════════════════════════════════════╝

┌─────────────────────────────────────────────────────────────────┐
│ ORIGINAL Activity:                                              │
│ TraceId:    abc123...                                           │
│ SpanId:     def456...                                           │
└─────────────────────────────────────────────────────────────────┘

LogRecord.TraceId: abc123...  ✅ MATCHES
LogRecord.SpanId:  def456...  ✅ MATCHES
```

## Integration with Akka.NET Core

To integrate this approach into Akka.NET:

### Phase 1: Core Changes (`Akka.NET`)

Add trace context capture to `LogEvent`:

```csharp
public abstract class LogEvent
{
    public ActivityTraceId? TraceId { get; }
    public ActivitySpanId? SpanId { get; }
    public ActivityTraceFlags TraceFlags { get; }

    protected LogEvent()
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            TraceId = activity.TraceId;
            SpanId = activity.SpanId;
            TraceFlags = activity.ActivityTraceFlags;
        }
    }
}
```

### Phase 2: Hosting Changes (`Akka.Hosting`)

Update `LoggerFactoryLogger` to use `AkkaLogState`:

```csharp
protected virtual void Log(LogEvent log, ActorPath path)
{
    if (log.TraceId.HasValue && log.SpanId.HasValue)
    {
        var state = new AkkaLogState(log.TraceId.Value, log.SpanId.Value,
                                      log.TraceFlags, log.Message.ToString());
        _logger.Log(GetLogLevel(log), new EventId(), state, log.Cause,
                    (s, _) => s.ToString());
    }
    else
    {
        // Fall back to standard logging
        _akkaLogger.Log(GetLogLevel(log), log.Message);
    }
}
```

## Other Logging Backends

| Backend | Native OTLP Correlation | Approach |
|---------|------------------------|----------|
| **LoggerFactoryLogger** | ✅ Yes | Use `AkkaTraceContextProcessor` |
| **Serilog → MEL** | ✅ Yes | Route through MEL + processor |
| **Serilog → Direct OTLP** | ❌ Attributes only | Use `LogContext.PushProperty` |
| **NLog → MEL** | ✅ Yes | Route through MEL + processor |
| **NLog → Direct OTLP** | ❌ Attributes only | Use `ScopeContext.PushProperty` |

For native OpenTelemetry `LogRecord.TraceId/SpanId` correlation (not just attributes), route all backends through `Microsoft.Extensions.Logging` with `AkkaTraceContextProcessor`.

## References

- [GitHub Issue #6855](https://github.com/akkadotnet/akka.net/issues/6855) - Original issue
- [dotnet/runtime#86966](https://github.com/dotnet/runtime/issues/86966) - ActivityContext.Current proposal
- [opentelemetry-dotnet#6085](https://github.com/open-telemetry/opentelemetry-dotnet/issues/6085) - Pass ActivityContext to ILogger

## License

Apache 2.0 - See [LICENSE](LICENSE) for details.
