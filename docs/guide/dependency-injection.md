# Dependency Injection

The `Nerdigy.Mediator.DependencyInjection` package connects Nerdigy.Mediator to `Microsoft.Extensions.DependencyInjection`. A single call to `AddMediator` scans your assemblies, discovers every handler and pipeline component, and registers them with the correct lifetime and cardinality -- no manual wiring required.

::: info
The core `Nerdigy.Mediator` package has no dependency on any DI framework. `Nerdigy.Mediator.DependencyInjection` is an optional add-on that provides the assembly scanning and service registration shown on this page.
:::

## Basic Setup

Pass a configuration callback to `AddMediator` and tell it which assemblies to scan.

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
});
```

`RegisterServicesFromAssemblyContaining<T>()` resolves the assembly that contains the marker type `T` and queues it for scanning. Any concrete, non-abstract class in that assembly implementing a mediator interface is registered automatically.

After calling `AddMediator`, resolve `IMediator`, `ISender`, or `IPublisher` from the container as usual:

```csharp
var mediator = serviceProvider.GetRequiredService<IMediator>();
```

### Multiple Assemblies

When handlers and pipeline components live across several projects, register each assembly explicitly.

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblies(
        typeof(CreateOrderHandler).Assembly,
        typeof(BillingNotificationHandler).Assembly);
});
```

A shorthand overload accepts assemblies directly, without the options callback:

```csharp
services.AddMediator(
    typeof(CreateOrderHandler).Assembly,
    typeof(BillingNotificationHandler).Assembly);
```

::: warning
`AddMediator` throws an `InvalidOperationException` if no assemblies are configured. At least one assembly must be registered for scanning.
:::

## What Gets Registered

The scanner inspects every concrete, non-abstract class in the configured assemblies and registers any type that implements a recognized mediator interface. The interfaces fall into two categories based on their registration semantics.

### Single Registration (one handler per request type)

These interfaces use `TryAdd`, which registers only the **first** implementation found for a given service type. A second handler for the same request type is silently ignored.

| Interface | Purpose |
| --- | --- |
| `IRequestHandler<TRequest, TResponse>` | Handles a request and returns a response |
| `IRequestHandler<TRequest>` | Handles a void request (no response) |
| `IStreamRequestHandler<TRequest, TResponse>` | Handles a stream request returning `IAsyncEnumerable<TResponse>` |

This enforces the mediator contract: each request type maps to exactly one handler.

### Multi Registration (multiple components per type)

These interfaces use `TryAddEnumerable`, which allows **multiple** implementations for the same service type while preventing exact duplicates.

| Interface | Purpose |
| --- | --- |
| `INotificationHandler<TNotification>` | Handles a notification (fan-out) |
| `IPipelineBehavior<TRequest, TResponse>` | Request pipeline middleware |
| `IStreamPipelineBehavior<TRequest, TResponse>` | Stream pipeline middleware |
| `IRequestPreProcessor<TRequest>` | Runs before the request handler |
| `IRequestPostProcessor<TRequest, TResponse>` | Runs after the request handler |
| `IRequestExceptionHandler<TRequest, TResponse, TException>` | Handles exceptions with recovery option |
| `IRequestExceptionAction<TRequest, TException>` | Side-effect on exception before rethrow |
| `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` | Handles stream request exceptions |

Notifications need multiple handlers by design. Pipeline components stack: every registered behavior, processor, or exception handler participates in the pipeline for its matching request type.

## Open-Generic Registration

The scanner supports open-generic types for all multi-registration interfaces. Define a generic pipeline component once and it applies to every matching request type without per-type wiring.

```csharp
public sealed class LoggingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // log before
        TResponse response = await next();
        // log after

        return response;
    }
}
```

Place this class in a scanned assembly and the scanner registers `IPipelineBehavior<,>` mapped to `LoggingBehavior<,>`. The DI container closes the generic at resolve time for each concrete request type.

For explicit behavior ordering in the options block, register open behaviors directly:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
    options.AddOpenBehavior(typeof(ValidationBehavior<,>));
    options.AddOpenBehavior(typeof(LoggingBehavior<,>));
});
```

The same pattern works for `IRequestPreProcessor<>`, `IRequestPostProcessor<,>`, `IStreamPipelineBehavior<,>`, exception handlers, and exception actions.

## Service Lifetimes

Two properties on `NerdigyMediatorOptions` control lifetimes:

| Property | Controls | Default |
| --- | --- | --- |
| `MediatorLifetime` | `IMediator`, `ISender`, `IPublisher` | `Transient` |
| `HandlerLifetime` | All scanned handlers and pipeline components | `Transient` |

Override them in the configuration callback:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
    options.MediatorLifetime = ServiceLifetime.Scoped;
    options.HandlerLifetime = ServiceLifetime.Scoped;
});
```

::: tip
If your handlers inject scoped services (like `DbContext`), set `HandlerLifetime` to `Scoped` to match their scope. Resolving a scoped dependency from a transient service works in some containers but produces subtle lifetime bugs in others.
:::

## Notification Publisher Strategy

By default, notifications are published sequentially using `ForeachAwaitPublisher`, which awaits each handler one at a time. Switch to parallel execution with `TaskWhenAllPublisher`:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
    options.UseNotificationPublisherStrategy(
        NerdigyMediatorNotificationPublisherStrategy.Parallel);
});
```

The two built-in strategies:

| Strategy | Behavior |
| --- | --- |
| `Sequential` (default) | Awaits each `INotificationHandler<T>` in sequence |
| `Parallel` | Starts all handlers concurrently via `Task.WhenAll` |

### Custom Publisher

For full control over dispatch ordering, throttling, or resilience, implement `INotificationPublisher` and register it by type or instance:

```csharp
// Register by type (resolved from the container)
options.UseNotificationPublisher<MyCustomPublisher>();

// Register a specific instance (singleton)
options.UseNotificationPublisher(new MyCustomPublisher());
```

When registering by instance, the publisher is registered as a singleton.

## Core Service Registrations

`AddMediator` registers the following core services in addition to scanned components:

| Service | Implementation | Lifetime |
| --- | --- | --- |
| `IMediator` | `Mediator` | `MediatorLifetime` |
| `ISender` | Forwarded to `IMediator` | `MediatorLifetime` |
| `IPublisher` | Forwarded to `IMediator` | `MediatorLifetime` |
| `INotificationPublisher` | Configured publisher type | `Singleton` |

All core registrations use `TryAdd`, so calling `AddMediator` multiple times does not produce duplicate registrations. The first call wins.
