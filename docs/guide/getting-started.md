# Getting Started

Nerdigy.Mediator gives you strongly-typed request/response dispatch, notification fan-out, and async streaming for .NET 10 applications. This guide takes you from installation to a working request and notification in under five minutes.

## Installation

Install the DI package. It pulls in the core runtime and abstractions transitively.

```bash
dotnet add package Nerdigy.Mediator.DependencyInjection
```

This gives you all three packages:

| Package | Purpose |
|---|---|
| `Nerdigy.Mediator.Abstractions` | Contracts: `IMediator`, `IRequest<T>`, `INotification`, handler interfaces |
| `Nerdigy.Mediator` | Runtime: dispatch, pipeline execution, notification publishers |
| `Nerdigy.Mediator.DependencyInjection` | DI registration via `services.AddMediator(...)` |

::: tip Only need contracts?
If you are defining requests and handlers in a library that should not depend on the runtime, reference `Nerdigy.Mediator.Abstractions` directly.
:::

## Register the Mediator

Call `AddMediator` on your `IServiceCollection` and tell it which assemblies to scan. The scanner finds your handlers, pipeline behaviors, processors, and exception components automatically.

```csharp
using Nerdigy.Mediator.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<Program>();
});
```

That single call registers `IMediator`, `ISender`, and `IPublisher` in the container. You can inject any of the three -- `IMediator` combines both sending and publishing, while `ISender` and `IPublisher` let you depend on only the capability you need.

## Send Your First Request

A request is a plain class or record that implements `IRequest<TResponse>`. A handler implements `IRequestHandler<TRequest, TResponse>` with a single `Handle` method.

### Define the request and handler

```csharp
using Nerdigy.Mediator.Abstractions;

public sealed record GetWeatherQuery(string City) : IRequest<WeatherForecast>;

public sealed class GetWeatherHandler : IRequestHandler<GetWeatherQuery, WeatherForecast>
{
    public Task<WeatherForecast> Handle(
        GetWeatherQuery request, CancellationToken cancellationToken)
    {
        WeatherForecast forecast = new(request.City, 22, "Sunny");

        return Task.FromResult(forecast);
    }
}

public sealed record WeatherForecast(string City, int TemperatureC, string Summary);
```

### Dispatch the request

Inject `ISender` (or `IMediator`) and call `Send`. The return type is inferred from the `IRequest<TResponse>` contract.

```csharp
app.MapGet("/weather/{city}", async (string city, ISender sender) =>
{
    WeatherForecast forecast = await sender.Send(new GetWeatherQuery(city));

    return Results.Ok(forecast);
});
```

One request, one handler, no manual wiring. The assembly scanner registered `GetWeatherHandler` when it found `IRequestHandler<GetWeatherQuery, WeatherForecast>`.

## Void Requests

Commands that produce no return value implement the non-generic `IRequest` and use `IRequestHandler<TRequest>` (single type parameter).

```csharp
public sealed record DeleteCityCommand(string City) : IRequest;

public sealed class DeleteCityHandler : IRequestHandler<DeleteCityCommand>
{
    public Task Handle(DeleteCityCommand request, CancellationToken cancellationToken)
    {
        // perform deletion
        return Task.CompletedTask;
    }
}
```

Dispatch with the same `Send` method:

```csharp
await sender.Send(new DeleteCityCommand("Springfield"));
```

## Publish a Notification

Notifications deliver a message to zero or more handlers. A notification implements `INotification`, and each handler implements `INotificationHandler<T>`.

### Define the notification and handlers

```csharp
using Nerdigy.Mediator.Abstractions;

public sealed record CityDeleted(string City) : INotification;

public sealed class AuditLogHandler : INotificationHandler<CityDeleted>
{
    public Task Handle(CityDeleted notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Audit: {notification.City} was deleted");

        return Task.CompletedTask;
    }
}

public sealed class CacheInvalidationHandler : INotificationHandler<CityDeleted>
{
    public Task Handle(CityDeleted notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Cache cleared for {notification.City}");

        return Task.CompletedTask;
    }
}
```

### Publish the notification

Inject `IPublisher` (or `IMediator`) and call `Publish`. Both handlers execute -- by default, sequentially in registration order.

```csharp
await publisher.Publish(new CityDeleted("Springfield"));
```

If no handlers are registered for a notification type, `Publish` completes successfully with no effect.

## Minimal Console Example

Here is a complete, self-contained program that sends a request and publishes a notification.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Nerdigy.Mediator.Abstractions;
using Nerdigy.Mediator.DependencyInjection;

ServiceCollection services = new();

services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<PingHandler>();
});

await using ServiceProvider provider = services.BuildServiceProvider();
IMediator mediator = provider.GetRequiredService<IMediator>();

// Request/response
string pong = await mediator.Send(new PingQuery("hello"));
Console.WriteLine(pong); // PONG: hello

// Notification
await mediator.Publish(new Pinged("hello"));

// --- Contracts and handlers ---

public sealed record PingQuery(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<PingQuery, string>
{
    public Task<string> Handle(PingQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"PONG: {request.Message}");
    }
}

public sealed record Pinged(string Message) : INotification;

public sealed class PingedHandler : INotificationHandler<Pinged>
{
    public Task Handle(Pinged notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Notification received: {notification.Message}");

        return Task.CompletedTask;
    }
}
```

## What's Next

You have requests dispatching and notifications publishing. Nerdigy.Mediator offers several more capabilities worth exploring:

- **[Dependency Injection](/guide/dependency-injection)** -- Assembly scanning options, service lifetimes, and notification publisher strategies.
- **[Requests](/guide/requests)** -- Request/response and void request patterns in depth.
- **[Notifications](/guide/notifications)** -- Sequential vs. parallel publishing and custom publisher strategies.
- **[Streaming](/guide/streaming)** -- Async streaming with `IStreamRequest<T>`, `IAsyncEnumerable<T>`, and cancellation semantics.
- **[Pipelines and Processors](/guide/pipelines)** -- Pre-processors, post-processors, and pipeline behaviors for cross-cutting concerns like logging and validation.
- **[Exception Handling](/guide/exception-handling)** -- Typed exception handlers that recover from errors and exception actions for side-effects before rethrow.
