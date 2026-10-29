---
layout: home

hero:
  name: Nerdigy.Mediator
  text: A mediator runtime for .NET 10
  tagline: Dispatch requests, publish notifications, and stream responses through strongly-typed pipelines. No per-call reflection, Native AOT ready, and traced with OpenTelemetry.
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
    - theme: alt
      text: LLM Quickstart
      link: /guide/llm-quickstart
    - theme: alt
      text: View Contracts
      link: /api/contracts

features:
  - title: Request / Response
    details: Send a request, get a typed response. Supports both return-value and void dispatch through a single IMediator interface.
    link: /guide/requests
  - title: Notification Fan-Out
    details: Publish a notification to every registered handler. Choose sequential execution or parallel dispatch with pluggable publisher strategies.
    link: /guide/notifications
  - title: Async Streaming
    details: Return IAsyncEnumerable responses via CreateStream with dedicated stream handlers, stream pipeline behaviors, and full cancellation propagation.
    link: /guide/streaming
  - title: Pipeline Middleware
    details: Compose pre-processors, pipeline behaviors, and post-processors in registration order. Each behavior wraps the next, following the same middleware pattern you already know.
    link: /guide/pipelines
  - title: Exception Recovery
    details: Catch failures with typed exception handlers that can mark exceptions as handled and supply a recovery response. Run side-effects with exception actions before rethrow.
    link: /guide/exception-handling
  - title: Scanning or Explicit Registration
    details: Point AddMediator at your assemblies, or list components explicitly. Duplicate handlers and components that could never run fail at startup.
    link: /guide/dependency-injection
  - title: Native AOT Ready
    details: All packages are trimming and AOT annotated. With explicit registration, a Native AOT app publishes without warnings.
    link: /guide/native-aot
  - title: Built-In Telemetry
    details: Activities and metrics for every send, publish, and stream enumeration, ready for OpenTelemetry and free when nothing listens.
    link: /guide/observability
---

## Minimal Example

```csharp
// Define a request and its handler
public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"PONG: {request.Message}");
    }
}
```

```csharp
// Register and dispatch
var services = new ServiceCollection();

services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<PingHandler>();
});

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

string response = await mediator.Send(new Ping("hello"));
// response == "PONG: hello"
```

## Three Packages, Clear Boundaries

| Package                                  | Purpose                                                                                                                                               |
| ---------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Nerdigy.Mediator.Contracts**           | Contracts only. `IMediator`, `ISender`, `IPublisher`, request/notification/stream interfaces, handler interfaces, pipeline and exception interfaces.  |
| **Nerdigy.Mediator**                     | Runtime. Cached typed dispatch, pipeline execution, exception processing, notification publishers, tracing and metrics. No DI framework dependency.    |
| **Nerdigy.Mediator.DependencyInjection** | Registration. `AddMediator(...)`, assembly scanning or explicit registration, startup validation, service lifetimes, publisher strategy selection.     |

## Next Steps

- **[LLM Quickstart](/guide/llm-quickstart)** -- Copy-ready prompts and templates to generate production-ready handlers, behaviors, and streams with minimal iteration.
- **[Decision Matrix](/guide/decision-matrix)** -- Pick the right interface and dispatch pattern (`Send`, `Publish`, `CreateStream`) quickly.
- **[Recipe Catalog](/guide/recipes)** -- Intent-based prompts for CQRS, domain events, streaming, validation, and exception recovery.
- **[Getting Started](/guide/getting-started)** -- Install packages, write your first handler, and dispatch a request in under five minutes.
- **[Requests](/guide/requests)** -- Request/response patterns, void requests, and handler conventions.
- **[Pipelines](/guide/pipelines)** -- Pre-processors, behaviors, post-processors, and execution order.
- **[API Contracts](/api/contracts)** -- Complete interface reference for every public type in the Contracts package.
- **[Migrating to 2.0](/guide/migrating-to-v2)** -- Breaking changes from 1.x and how to upgrade.
