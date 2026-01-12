#### 1.0.0 January 12 2026 ####

Initial proof of concept demonstrating Akka.NET log/trace correlation with OpenTelemetry.

**Key Components:**
- `AkkaLogState` - Structured log state capturing ActivityContext
- `AkkaTraceContextProcessor` - LogRecordProcessor that sets TraceId/SpanId directly

**Problem Solved:**
`Activity.Current` doesn't flow across actor mailbox boundaries. This PoC demonstrates how to capture ActivityContext before the mailbox crossing and restore it in logs without creating child spans.

**Related Issue:** [akkadotnet/akka.net#6855](https://github.com/akkadotnet/akka.net/issues/6855)
