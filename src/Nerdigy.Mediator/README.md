# Nerdigy.Mediator

The runtime for [Nerdigy.Mediator](https://github.com/nerdigy/mediator): request/response and void requests, notification fan-out, async streams, pipeline behaviors, pre- and post-processors, typed exception handling, and tracing and metrics. It has no dependency on a DI framework and supports Native AOT.

Most applications reference `Nerdigy.Mediator.DependencyInjection` instead, which includes this package and registers everything with `AddMediator`. Reference this package directly to construct the mediator over any `IServiceProvider`.

```bash
dotnet add package Nerdigy.Mediator
```

```csharp
using Nerdigy.Mediator;
using Nerdigy.Mediator.Contracts;

// Under Native AOT, register each message type's dispatch code at startup.
MediatorRegistry.RegisterRequest<GetUserQuery, UserDto>();

IMediator mediator = new Mediator(serviceProvider, new ForeachAwaitPublisher());
UserDto user = await mediator.Send(new GetUserQuery(id), cancellationToken);
```

Traces and metrics are reported through the `Nerdigy.Mediator` activity source and meter (`MediatorTelemetry.ActivitySourceName`, `MediatorTelemetry.MeterName`), and cost only a listener check when nothing listens.

Documentation: [runtime behavior](https://github.com/nerdigy/mediator/blob/main/docs/api/runtime-behavior.md) · [Native AOT](https://github.com/nerdigy/mediator/blob/main/docs/guide/native-aot.md) · [observability](https://github.com/nerdigy/mediator/blob/main/docs/guide/observability.md) · [changelog](https://github.com/nerdigy/mediator/blob/main/CHANGELOG.md)
