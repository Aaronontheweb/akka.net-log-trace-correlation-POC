using System.Diagnostics;
using Akka.Actor;
using Akka.LogTraceCorrelation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

// =============================================================================
// Akka.NET Log/Trace Correlation Proof of Concept
// =============================================================================
// This PoC demonstrates how to correlate Akka.NET actor logs with OpenTelemetry
// traces, solving the problem that Activity.Current doesn't flow across actor
// mailbox boundaries.
//
// KEY INSIGHT: We don't try to restore Activity.Current (which would create
// child spans). Instead, we:
// 1. Capture ActivityContext at message send time
// 2. Pass it through ILogger as structured state (AkkaLogState)
// 3. Use AkkaTraceContextProcessor to set LogRecord.TraceId/SpanId DIRECTLY
//
// This preserves the EXACT original TraceId AND SpanId - no child spans!
// =============================================================================

// Create activity source for the demo
var activitySource = new ActivitySource("Akka.LogTraceCorrelation.Demo");

// Add listener to enable activity creation
var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == "Akka.LogTraceCorrelation.Demo",
    Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData
};
ActivitySource.AddActivityListener(listener);

var builder = Host.CreateApplicationBuilder(args);

// Configure OpenTelemetry logging with our custom processor
builder.Logging.ClearProviders();
builder.Logging.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService("akka-log-trace-correlation-poc"));

    // CRITICAL: Register our processor FIRST to set TraceId/SpanId
    // before any exporters process the LogRecord
    options.AddProcessor(new AkkaTraceContextProcessor());

    // Parse state values to capture our custom AkkaLogState attributes
    options.ParseStateValues = true;
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;

    // Export to console for visibility
    options.AddConsoleExporter();
});

var host = builder.Build();

// Get logger factory
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

// Create actor system
var actorSystem = ActorSystem.Create("DemoSystem");

// Create actor with injected logger
var loggingActor = actorSystem.ActorOf(
    Props.Create(() => new LoggingActor(loggerFactory.CreateLogger<LoggingActor>())),
    "logging-actor");

Console.WriteLine();
Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Akka.NET Log/Trace Correlation Proof of Concept               ║");
Console.WriteLine("╠════════════════════════════════════════════════════════════════╣");
Console.WriteLine("║  This demonstrates correlating actor logs with OpenTelemetry   ║");
Console.WriteLine("║  traces WITHOUT creating child spans.                          ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");
Console.WriteLine();

// Start an activity (span) to trace
using (var activity = activitySource.StartActivity("ProcessRequest", ActivityKind.Internal))
{
    if (activity == null)
    {
        Console.WriteLine("ERROR: Failed to start activity. Ensure ActivityListener is registered.");
        return;
    }

    var originalTraceId = activity.TraceId;
    var originalSpanId = activity.SpanId;

    Console.WriteLine("┌─────────────────────────────────────────────────────────────────┐");
    Console.WriteLine("│ ORIGINAL Activity (the trace we want logs correlated to):       │");
    Console.WriteLine("├─────────────────────────────────────────────────────────────────┤");
    Console.WriteLine($"│ TraceId:    {originalTraceId}              │");
    Console.WriteLine($"│ SpanId:     {originalSpanId}                              │");
    Console.WriteLine($"│ TraceFlags: {activity.ActivityTraceFlags,-49} │");
    Console.WriteLine("└─────────────────────────────────────────────────────────────────┘");
    Console.WriteLine();

    Console.WriteLine("Sending 3 messages to actor (crossing mailbox boundary)...");
    Console.WriteLine("Watch for [AkkaTraceContextProcessor] setting LogRecord values.");
    Console.WriteLine();

    for (int i = 1; i <= 3; i++)
    {
        // Create message with trace context captured NOW (before mailbox crossing)
        var tracedMessage = new TracedMessage($"Message {i}", activity.Context);

        // Send to actor - Activity.Current will be NULL when actor processes this!
        // But the ActivityContext is preserved in the message.
        var response = await loggingActor.Ask<string>(tracedMessage, TimeSpan.FromSeconds(5));
        Console.WriteLine($"  Response: {response}");
    }

    // Give time for logs to flush
    await Task.Delay(500);

    Console.WriteLine();
    Console.WriteLine("┌─────────────────────────────────────────────────────────────────┐");
    Console.WriteLine("│ VALIDATION                                                       │");
    Console.WriteLine("├─────────────────────────────────────────────────────────────────┤");
    Console.WriteLine("│ Check the LogRecord output above. ALL logs should have:         │");
    Console.WriteLine($"│   TraceId: {originalTraceId}              │");
    Console.WriteLine($"│   SpanId:  {originalSpanId}                              │");
    Console.WriteLine("│                                                                  │");
    Console.WriteLine("│ If SpanIds were DIFFERENT, that would indicate child spans      │");
    Console.WriteLine("│ were created (the bug we're avoiding).                          │");
    Console.WriteLine("│                                                                  │");
    Console.WriteLine("│ If SpanIds are ALL THE SAME, the correlation works correctly!   │");
    Console.WriteLine("└─────────────────────────────────────────────────────────────────┘");
}

// Cleanup
await actorSystem.Terminate();
await host.StopAsync();

Console.WriteLine();
Console.WriteLine("PoC Complete.");
