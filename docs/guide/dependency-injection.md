# Dependency Injection

The `Nerdigy.Mediator.DependencyInjection` package connects Nerdigy.Mediator to `Microsoft.Extensions.DependencyInjection`. `AddMediator` registers the mediator and every handler and pipeline component, either by scanning your assemblies, from an explicit list, or both. It also checks the registrations and fails at startup when one would misbehave at runtime.

::: info
The core `Nerdigy.Mediator` package has no dependency on any DI framework. `Nerdigy.Mediator.DependencyInjection` is an optional add-on that provides the registration shown on this page.
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

After calling `AddMediator`, resolve `IMediator`, `ISender`, or `IPublisher` from a scope:

```csharp
using var scope = serviceProvider.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
```

::: tip
Always resolve `IMediator` from a scope. A mediator resolved from the root provider resolves its handlers from the root too, and the root keeps every transient `IDisposable` handler it creates alive until the root itself is disposed.
:::

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

## Explicit Registration

Every component can also be registered explicitly. Explicit registration is required for trimmed and Native AOT apps (see [Native AOT](/guide/native-aot)), because it also registers each message type's dispatch code ahead of time. Use it anywhere you would rather list components than scan for them.

```csharp
services.AddMediator(options => options
    .AddRequestHandler<GetUserQuery, UserDto, GetUserQueryHandler>()
    .AddRequestHandler<DeleteUserCommand, DeleteUserCommandHandler>() // void request
    .AddStreamRequestHandler<GetOrdersStream, OrderDto, GetOrdersStreamHandler>()
    .AddNotificationHandler<UserCreatedNotification, SendWelcomeEmailHandler>()
    .AddBehavior<GetUserQuery, UserDto, CachingBehavior>()
    .AddOpenBehavior(typeof(LoggingBehavior<,>))
    .AddPreProcessor<GetUserQuery, AuditPreProcessor>()
    .AddPostProcessor<GetUserQuery, UserDto, MetricsPostProcessor>()
    .AddExceptionHandler<GetUserQuery, UserDto, NotFoundException, NotFoundFallback>()
    .AddExceptionAction<GetUserQuery, Exception, LogFailureAction>());
```

| Method | Registers |
| --- | --- |
| `AddRequestHandler<TRequest, TResponse, THandler>()` | `IRequestHandler<TRequest, TResponse>` |
| `AddRequestHandler<TRequest, THandler>()` | `IRequestHandler<TRequest>` for a void request |
| `AddStreamRequestHandler<TRequest, TResponse, THandler>()` | `IStreamRequestHandler<TRequest, TResponse>` |
| `AddNotificationHandler<TNotification, THandler>()` | `INotificationHandler<TNotification>` |
| `AddBehavior<TRequest, TResponse, TBehavior>()` | A closed `IPipelineBehavior<TRequest, TResponse>` |
| `AddStreamBehavior<TRequest, TResponse, TBehavior>()` | A closed `IStreamPipelineBehavior<TRequest, TResponse>` |
| `AddOpenBehavior(Type)` | An open generic `IPipelineBehavior<,>` |
| `AddOpenStreamBehavior(Type)` | An open generic `IStreamPipelineBehavior<,>` |
| `AddPreProcessor<TRequest, TProcessor>()` | `IRequestPreProcessor<TRequest>` |
| `AddPostProcessor<TRequest, TResponse, TProcessor>()` | `IRequestPostProcessor<TRequest, TResponse>` |
| `AddExceptionHandler<TRequest, TResponse, TException, THandler>()` | `IRequestExceptionHandler<TRequest, TResponse, TException>` |
| `AddExceptionAction<TRequest, TException, TAction>()` | `IRequestExceptionAction<TRequest, TException>` |
| `AddStreamExceptionHandler<TRequest, TResponse, TException, THandler>()` | `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` |

Every method takes an optional `ServiceLifetime`. Without one, the component uses `HandlerLifetime`.

Explicit registration and scanning can be combined. An explicitly registered handler replaces a scanned handler for the same request, which is the supported way to override one.

### Without Scanning or Options

`AddMediator()` with no arguments registers only the mediator and its notification publisher. Use it when you register handlers directly in the service collection:

```csharp
services.AddTransient<IRequestHandler<GetUserQuery, UserDto>, GetUserQueryHandler>();
services.AddMediator();
```

## What Gets Registered

The scanner inspects every concrete, non-abstract class in the configured assemblies and registers any type that implements a recognized mediator interface. Types are scanned in order of full type name, so the order of scanned behaviors does not depend on how the compiler laid out the assembly. The interfaces fall into two categories based on their registration semantics.

### Single Registration (one handler per request type)

Each request type has exactly one handler.

| Interface | Purpose |
| --- | --- |
| `IRequestHandler<TRequest, TResponse>` | Handles a request and returns a response |
| `IRequestHandler<TRequest>` | Handles a void request (no response) |
| `IStreamRequestHandler<TRequest, TResponse>` | Handles a stream request returning `IAsyncEnumerable<TResponse>` |

`AddMediator` enforces this:

- Two different handlers for the same request fail registration, whether both were scanned, both were registered explicitly, or they came from separate `AddMediator` calls.
- An explicitly registered handler replaces a scanned one.
- A handler registered directly in the service collection before `AddMediator` is kept, and the scanned handler for that request is skipped.

A request with no handler at all is only detected when it is first sent, because `AddMediator` cannot know which request types your app sends.

### Multi Registration (multiple components per type)

These interfaces allow **multiple** implementations for the same service type. Registering the same implementation twice has no effect.

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

### Behavior Order

Behaviors run in registration order, the first registered being the outermost:

1. Behaviors added with `AddBehavior` and `AddOpenBehavior`, in the order you call them.
2. Scanned behaviors, in order of full type name.

## Registration Errors

`AddMediator` fails with one `InvalidOperationException` that lists every problem it found, so a misconfiguration surfaces at startup instead of on the first request that hits it.

| Problem | Fix |
| --- | --- |
| Two different handlers for the same request | Keep one, or register the one you want explicitly to override the scanned one |
| A component closed over an interface or abstract message type, such as `AuditBehavior : IPipelineBehavior<IAuditable, Unit>` or `AllHandler : INotificationHandler<INotification>` | Declare it as an open generic constrained to that type (see below) |
| `IRequestHandler<TRequest, Unit>` for a request that implements `IRequest` | Implement `IRequestHandler<TRequest>` |
| A scanned assembly with types that fail to load | Fix the missing dependency, or set `options.IgnoreTypeLoadFailures = true` to scan the types that loaded |
| An open generic whose type parameters do not match the interface (see [Supported Open-Generic Shapes](#supported-open-generic-shapes)) | Reorder its type parameters, or close it for a specific request |
| A singleton mediator combined with scoped components (see [Service Lifetimes](#service-lifetimes)) | Use a `Transient` or `Scoped` mediator |

The explicit registration methods check the same rules and throw an `ArgumentException` immediately.

### Components for an Interface or Base Message Type

The mediator resolves components for a message's concrete runtime type, and the container does not apply variance. A component closed over an interface would register without error and then never run, so `AddMediator` rejects it. Declare it as an open generic constrained to the interface instead:

```csharp
// Rejected: never runs for any concrete request.
public sealed class AuditBehavior : IPipelineBehavior<IAuditable, Unit> { /* ... */ }

// Runs for every request that implements IAuditable.
public sealed class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IAuditable
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // audit
        return next(cancellationToken);
    }
}
```

The same applies to notifications: a handler for every notification is `AllNotificationsHandler<TNotification> : INotificationHandler<TNotification> where TNotification : INotification`.

A handler for a non-abstract base class is accepted, because the base class can itself be sent or published. It only runs for that exact type, not for derived types.

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
        TResponse response = await next(cancellationToken);
        // log after

        return response;
    }
}
```

Place this class in a scanned assembly and the scanner registers `IPipelineBehavior<,>` mapped to `LoggingBehavior<,>`. The DI container closes the generic at resolve time for each concrete request type, and skips request types that do not satisfy its constraints.

For explicit behavior ordering in the options block, register open behaviors directly:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
    options.AddOpenBehavior(typeof(ValidationBehavior<,>));
    options.AddOpenBehavior(typeof(LoggingBehavior<,>));
});
```

`AddOpenBehavior` accepts only open-generic `IPipelineBehavior<,>` types, and `AddOpenStreamBehavior` only open-generic `IStreamPipelineBehavior<,>` types. Both throw an `ArgumentException` for other types. To register other open-generic components, such as preprocessors or exception actions, put them in a scanned assembly.

::: warning
Under Native AOT, the container cannot close an open generic over a value type, including `Unit` for void requests. See [Native AOT](/guide/native-aot#open-generic-components).
:::

### Supported Open-Generic Shapes

The DI container closes an open generic by passing the service interface's type arguments to the implementation **positionally**. An open-generic component is therefore supported only when its type parameters are exactly the interface's type arguments, one-to-one and in the same order.

| Declaration | Supported | Why |
| --- | --- | --- |
| `LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>` | Yes | Parameters map positionally onto the interface |
| `AuditPreProcessor<TRequest> : IRequestPreProcessor<TRequest>` | Yes | Parameters map positionally onto the interface |
| `StringBehavior<TRequest> : IPipelineBehavior<TRequest, string>` | No | Partially closed: the interface has a fixed type argument |
| `ReversedBehavior<TResponse, TRequest> : IPipelineBehavior<TRequest, TResponse>` | No | Parameters are declared in a different order |
| `LogAllExceptions<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse, Exception>` | No | Partially closed: the exception type is fixed |

`AddMediator` lists every unsupported open-generic component found while scanning, and `AddOpenBehavior` throws an `ArgumentException` for an unsupported behavior type. To fix one, reorder its type parameters to match the interface, or close it for a specific request (for example, `GetUserBehavior : IPipelineBehavior<GetUserQuery, UserDto>`).

## Service Lifetimes

Two properties on `MediatorOptions` control lifetimes:

| Property | Controls | Default |
| --- | --- | --- |
| `MediatorLifetime` | `IMediator`, `ISender`, `IPublisher` | `Transient` |
| `HandlerLifetime` | Scanned components, and explicit ones registered without a lifetime | `Transient` |

Override them in the configuration callback:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
    options.MediatorLifetime = ServiceLifetime.Scoped;
    options.HandlerLifetime = ServiceLifetime.Scoped;
});
```

What matters for scoped dependencies like `DbContext` is the scope the handler is resolved from, not the handler's own lifetime. A transient handler resolved through a mediator from a request scope receives that scope's `DbContext`. Resolve `IMediator` from a scope and transient handlers work with scoped dependencies.

::: warning
A singleton mediator resolves every handler from the root service provider, which cannot resolve scoped services. `AddMediator` therefore throws when `MediatorLifetime` is `Singleton` and `HandlerLifetime`, an explicit registration's lifetime, or the notification publisher's lifetime is `Scoped`. Use `Scoped` or `Transient` for `MediatorLifetime` and resolve `IMediator` from a scope.
:::

## Notification Publisher Strategy

By default, notifications are published sequentially using `ForeachAwaitPublisher`, which awaits each handler one at a time. Switch to parallel execution with `TaskWhenAllPublisher`:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
    options.UseNotificationPublishStrategy(NotificationPublishStrategy.Parallel);
});
```

The two built-in strategies:

| Strategy | Behavior |
| --- | --- |
| `Sequential` (default) | Awaits each `INotificationHandler<T>` in sequence |
| `Parallel` | Starts all handlers concurrently via `Task.WhenAll` |

Parallel handlers are resolved from the same scope, so they share its scoped services. Handlers that run concurrently must not use a shared non-thread-safe service such as a `DbContext` at the same time.

### Custom Publisher

For full control over dispatch ordering, throttling, or resilience, implement `INotificationPublisher` and register it by type or instance:

```csharp
// Register by type (resolved from the container, singleton by default)
options.UseNotificationPublisher<MyCustomPublisher>();

// Register by type with another lifetime
options.UseNotificationPublisher<MyCustomPublisher>(ServiceLifetime.Scoped);

// Register a specific instance (singleton)
options.UseNotificationPublisher(new MyCustomPublisher());
```

A singleton publisher is shared across scopes and threads, so it must be safe to share. A scoped publisher cannot be combined with a singleton mediator.

## Core Service Registrations

`AddMediator` registers the following core services in addition to scanned components:

| Service | Implementation | Lifetime |
| --- | --- | --- |
| `IMediator` | `Mediator` | `MediatorLifetime` |
| `ISender` | Forwarded to `IMediator` | `MediatorLifetime` |
| `IPublisher` | Forwarded to `IMediator` | `MediatorLifetime` |
| `INotificationPublisher` | Configured publisher | `Singleton`, or the lifetime passed to `UseNotificationPublisher<TPublisher>` |

## Calling AddMediator More Than Once

`AddMediator` can be called more than once, for example once per module, each registering its own handlers:

```csharp
services.AddMediator(options => options.RegisterServicesFromAssemblyContaining<OrdersModule>());
services.AddMediator(options => options.RegisterServicesFromAssemblyContaining<BillingModule>());
```

The first call registers the mediator and sets `MediatorLifetime` and the notification publisher. A later call may leave them unset or repeat the same values; setting a different value throws. Duplicate handlers across calls are reported like duplicates within one call.
