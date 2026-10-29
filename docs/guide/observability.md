# Observability

Nerdigy.Mediator reports traces and metrics through `System.Diagnostics`: an `ActivitySource` and a `Meter`, both named `Nerdigy.Mediator`. Nothing is recorded until something listens, and until then each operation pays only a listener check.

## OpenTelemetry Setup

Add the source and meter by name. The names are constants on `MediatorTelemetry` in the `Nerdigy.Mediator` namespace.

```csharp
using Nerdigy.Mediator;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(MediatorTelemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(MediatorTelemetry.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

The core package has no OpenTelemetry dependency. Any `ActivityListener` or `MeterListener` works too.

On .NET 11, the rule-based tracing configuration in `Microsoft.Extensions.Diagnostics` can turn the source on or off by its name, `Nerdigy.Mediator`, like any other source. The mediator needs no extra integration for it.

## Traces

Every operation produces one activity of kind `Internal`:

| Operation | Activity name | Display name |
| --- | --- | --- |
| `Send` | `Nerdigy.Mediator.Send` | `send GetUserQuery` |
| `Publish` | `Nerdigy.Mediator.Publish` | `publish UserCreated` |
| Stream enumeration | `Nerdigy.Mediator.Stream` | `stream GetOrdersStream` |

Activities carry these tags:

| Tag | Value |
| --- | --- |
| `nerdigy.mediator.operation` | `send`, `publish`, or `stream` |
| `nerdigy.mediator.message.type` | Full name of the request or notification type |
| `nerdigy.mediator.handler.count` | On publish: how many handlers the notification reached |
| `nerdigy.mediator.exception.handled` | `true` when an exception handler recovered from a failure |
| `nerdigy.mediator.outcome` | `canceled` when the caller canceled |
| `error.type` | Full name of the exception type, when the operation failed |

A failed operation sets the activity status to `Error` and records the exception as an activity event. A cancellation the caller requested is not an error: the status stays unset and `nerdigy.mediator.outcome` is `canceled`. When an exception handler recovers, the activity also gets a `nerdigy.mediator.exception_handled` event with the `exception.type`.

Handlers, behaviors, and the code they call run inside the activity, so their own activities, such as database or HTTP calls, become its children.

### Streams

`CreateStream` returns without starting anything. The stream's activity starts when enumeration starts and stops when enumeration completes, fails, or the consumer stops early and disposes the enumerator. Its duration covers the whole enumeration, including time the consumer spends between items. Each enumeration of the same stream gets its own activity.

## Metrics

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `nerdigy.mediator.operation.duration` | Histogram (`double`) | `s` | operation, message type, outcome, `error.type` on errors |
| `nerdigy.mediator.operation.active` | Up-down counter (`long`) | `{operation}` | operation, message type |
| `nerdigy.mediator.publish.handlers` | Histogram (`int`) | `{handler}` | none |

The outcome tag, `nerdigy.mediator.outcome`, is `success`, `error`, or `canceled`. A request an exception handler recovered from counts as `success`; the activity's `nerdigy.mediator.exception.handled` tag tells it apart. A stream the consumer stopped early is a `success`.

Instrument and tag names are constants on `MediatorTelemetry`, such as `MediatorTelemetry.OperationDurationInstrument` and `MediatorTelemetry.OutcomeTag`.

## What Is Not Recorded

Telemetry never records request or notification contents. Tag values are limited to operation names, type names, outcomes, and exception type names, so their cardinality is bounded by the number of message types in your app.

## Logging

The core package does no logging and has no `ILogger` dependency. To log every request, add a logging behavior:

```csharp
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            TResponse response = await next(cancellationToken);
            logger.LogInformation("Handled {RequestType} in {Elapsed}", typeof(TRequest).Name, Stopwatch.GetElapsedTime(started));

            return response;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed {RequestType} after {Elapsed}", typeof(TRequest).Name, Stopwatch.GetElapsedTime(started));

            throw;
        }
    }
}
```

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<Program>();
    options.AddOpenBehavior(typeof(LoggingBehavior<,>));
});
```

To log only failures that no exception handler recovered from, use an open generic exception action instead; see [Exception Handling](/guide/exception-handling#open-generic-registration).
