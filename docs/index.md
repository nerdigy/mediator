---
layout: home

hero:
  name: Nerdigy.Mediator
  text: A mediator runtime for .NET 10
  tagline: Dispatch requests, publish notifications, and stream responses through strongly-typed pipelines with expression-compiled caching that eliminates per-call reflection.
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
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
  - title: Zero-Config Assembly Scanning
    details: Call AddMediator and point it at your assemblies. Handlers, behaviors, processors, and exception components are discovered and registered automatically.
    link: /guide/dependency-injection
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
var mediator = provider.GetRequiredService<IMediator>();

string response = await mediator.Send(new Ping("hello"));
// response == "PONG: hello"
```

## Three Packages, Clear Boundaries

| Package | Purpose |
|---|---|
| **Nerdigy.Mediator.Abstractions** | Contracts only. `IMediator`, `ISender`, `IPublisher`, request/notification/stream interfaces, handler interfaces, pipeline and exception interfaces. |
| **Nerdigy.Mediator** | Runtime. Dispatchers with expression-compiled caching, pipeline executors, exception processors, notification publishers. No DI framework dependency. |
| **Nerdigy.Mediator.DependencyInjection** | Registration. `AddMediator(...)`, assembly scanning, configurable service lifetimes, publisher strategy selection. |

## Next Steps

- **[Getting Started](/guide/getting-started)** -- Install packages, write your first handler, and dispatch a request in under five minutes.
- **[Requests](/guide/requests)** -- Request/response patterns, void requests, and handler conventions.
- **[Pipelines](/guide/pipelines)** -- Pre-processors, behaviors, post-processors, and execution order.
- **[API Contracts](/api/contracts)** -- Complete interface reference for every public type in the Abstractions package.
