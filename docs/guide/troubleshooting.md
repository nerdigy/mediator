# Troubleshooting

This page covers the most common issues you will encounter when using Nerdigy.Mediator, organized by symptom. Each section includes the error message you will see, the root cause, and how to fix it.

## LLM Failure Map

Use this condensed map when an assistant needs quick diagnosis:

| Symptom | Likely Cause | Fast Fix |
|---|---|---|
| `No assemblies were configured for mediator scanning` | `AddMediator` called without assembly registration | Add `options.RegisterServicesFromAssemblyContaining<...>()` |
| `No request handler is registered` | Handler class not scanned, wrong interface, or mismatched generic arguments | Verify interface type parameters and scanned assembly |
| Notification `Publish` does nothing | No `INotificationHandler<T>` registered for type | Add handler and ensure assembly is scanned |
| Behavior does not run | Behavior generic arguments do not match request type | Use exact request/response types or open generic behavior |
| Handler never runs | Behavior skipped `await next()` | Call `next()` unless intentionally short-circuiting |
| Exception handler runs but exception still thrown | `state.SetHandled(...)` not called | Set handled state with fallback response |
| Stream abruptly stops on exception | No stream exception handler for thrown type | Add `IStreamRequestExceptionHandler<TRequest,TResponse,TException>` |

## Registration Errors

### No assemblies configured for scanning

**Error message:**

```
System.InvalidOperationException:
No assemblies were configured for mediator scanning.
Call RegisterServicesFromAssembly(...) or RegisterServicesFromAssemblies(...)
inside AddMediator(options => ...).
```

**Cause:** You called `AddMediator` without telling the scanner which assemblies to inspect.

**Fix:** Add at least one assembly registration inside the options callback.

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<MyHandler>();
});
```

Or use the shorthand overload that accepts assemblies directly:

```csharp
services.AddMediator(typeof(MyHandler).Assembly);
```

### Handler not found for a request type

**Error message (request with response):**

```
System.InvalidOperationException:
No request handler is registered for request type
'MyApp.GetOrderQuery' and response type 'MyApp.OrderDto'.
Register IRequestHandler<GetOrderQuery, OrderDto> in your
dependency injection container.
```

**Error message (void request):**

```
System.InvalidOperationException:
No request handler is registered for request type
'MyApp.DeleteOrderCommand'.
Register IRequestHandler<DeleteOrderCommand> in your
dependency injection container.
```

**Error message (stream request):**

```
System.InvalidOperationException:
No stream request handler is registered for request type
'MyApp.StreamOrdersQuery' and response type 'MyApp.OrderDto'.
Register IStreamRequestHandler<StreamOrdersQuery, OrderDto> in your
dependency injection container.
```

**Cause:** The runtime resolved no handler from the DI container for the dispatched request type. This happens for one of several reasons:

- The handler class exists but is not in a scanned assembly.
- The handler class is `abstract` or is not `public`.
- The generic type parameters on the handler do not match the request and response types exactly.
- The handler was not discovered because the assembly was not passed to `RegisterServicesFromAssembly`.

**Fix:**

1. Verify the handler class is a concrete, non-abstract class.
2. Confirm the handler implements the correct interface with matching type parameters. For example, if your request is `GetOrderQuery : IRequest<OrderDto>`, the handler must implement `IRequestHandler<GetOrderQuery, OrderDto>` -- not `IRequestHandler<GetOrderQuery, object>` or any other variation.
3. Confirm the handler's assembly is registered for scanning:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<GetOrderHandler>();
});
```

4. If handlers live in multiple assemblies, register each one:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblies(
        typeof(GetOrderHandler).Assembly,
        typeof(BillingHandler).Assembly);
});
```

### Second handler for the same request type is silently ignored

**Symptom:** You registered two handlers for the same request type, but only the first one executes.

**Cause:** Request handlers (`IRequestHandler<,>`, `IRequestHandler<>`, and `IStreamRequestHandler<,>`) use `TryAdd` semantics. The scanner registers the **first** implementation it finds for a given service type and silently skips duplicates. This enforces the mediator contract: one request type maps to exactly one handler.

**Fix:** The mediator pattern requires a single handler per request type. If you need to run multiple operations for one request, use one of these approaches:

- **Pipeline behaviors** -- wrap the handler with `IPipelineBehavior<TRequest, TResponse>` for cross-cutting concerns.
- **Notifications** -- publish an `INotification` from within your handler to fan out to multiple notification handlers.

### Open-generic registration does not resolve

**Symptom:** You defined an open-generic pipeline component (e.g., `LoggingBehavior<TRequest, TResponse>`), but it does not execute for your requests.

**Cause:** The scanner registers open-generic types for multi-registration interfaces only. Open-generic registration requires:

- The class must be a generic type definition (e.g., `class LoggingBehavior<TRequest, TResponse>`).
- The class must implement one of the multi-registration interfaces: `INotificationHandler<>`, `IPipelineBehavior<,>`, `IStreamPipelineBehavior<,>`, `IRequestPreProcessor<>`, `IRequestPostProcessor<,>`, `IRequestExceptionHandler<,,>`, `IRequestExceptionAction<,>`, or `IStreamRequestExceptionHandler<,,>`.
- The class must be in a scanned assembly.

**Fix:**

1. Verify your class is a proper generic type definition with unclosed type parameters.
2. Verify the class is not abstract.
3. Confirm the class has the correct generic constraints. For example, a request behavior requires:

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
        // cross-cutting logic
        TResponse response = await next();

        return response;
    }
}
```

::: warning
Open-generic registration is not supported for single-registration interfaces (`IRequestHandler<,>`, `IRequestHandler<>`, `IStreamRequestHandler<,>`). Each request type must have exactly one concrete handler.
:::

## Pipeline Issues

### Behaviors are not executing

**Symptom:** You registered an `IPipelineBehavior<TRequest, TResponse>` but it never runs.

**Cause:** Pipeline behaviors must match the exact `TRequest` and `TResponse` type parameters of the request being dispatched. A behavior registered for `IPipelineBehavior<GetOrderQuery, OrderDto>` will not execute for a `GetUserQuery`.

**Fix:**

1. For behaviors that target a specific request, verify the type parameters match exactly.
2. For behaviors that should apply to all requests, use an open-generic registration:

```csharp
public sealed class TimingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TResponse response = await next();
        stopwatch.Stop();
        Console.WriteLine($"{typeof(TRequest).Name}: {stopwatch.ElapsedMilliseconds}ms");

        return response;
    }
}
```

3. Confirm the behavior's assembly is in the scanned assembly list.

### Behavior does not call next -- pipeline short-circuits

**Symptom:** The request handler never executes. `Send` returns a default or unexpected value.

**Cause:** A pipeline behavior returned a response without calling `next()`. The `next` delegate invokes the next behavior in the chain (or the handler itself, if this is the innermost behavior). Skipping it short-circuits the rest of the pipeline.

**Fix:** Ensure every behavior calls `await next()` unless you intentionally want to short-circuit:

```csharp
public async Task<TResponse> Handle(
    TRequest request,
    RequestHandlerDelegate<TResponse> next,
    CancellationToken cancellationToken)
{
    // pre-processing
    TResponse response = await next(); // <-- must call next
    // post-processing

    return response;
}
```

### Pipeline execution order

The request pipeline executes in this order:

1. **Pre-processors** (`IRequestPreProcessor<TRequest>`) -- in registration order
2. **Pipeline behaviors** (`IPipelineBehavior<TRequest, TResponse>`) -- outer-to-inner in registration order, wrapping the handler
3. **Request handler** (`IRequestHandler<TRequest, TResponse>`)
4. **Post-processors** (`IRequestPostProcessor<TRequest, TResponse>`) -- in registration order

On exception: **Exception handlers** run first (most-specific to least-specific exception type), then **exception actions**, then rethrow.

If the order of your behaviors matters, control registration order by registering them manually before calling `AddMediator`, or structure your scanned assemblies so the scanner encounters them in the intended sequence. The scanner processes each assembly in the order you provide.

## Exception Handling Issues

### Exception handler does not catch the expected exception

**Symptom:** You implemented `IRequestExceptionHandler<TRequest, TResponse, TException>`, but your handler is never invoked.

**Cause:** Exception handlers match on all three type parameters. The `TRequest`, `TResponse`, and `TException` must all align with the request being dispatched and the actual exception type thrown.

**Fix:**

1. Verify the `TRequest` parameter matches the dispatched request type.
2. Verify the `TResponse` parameter matches the request's response type.
3. Verify the `TException` parameter is the same type as the thrown exception, or a base type. The runtime walks the exception type hierarchy from most-specific to least-specific:

```
ArgumentNullException -> ArgumentException -> SystemException -> Exception
```

A handler registered for `Exception` catches everything. A handler registered for `ArgumentNullException` only catches that specific type.

4. Confirm the handler class is in a scanned assembly. Exception handlers use `TryAddEnumerable` -- multiple handlers for the same combination are allowed.

### Exception handler runs but the exception still propagates

**Symptom:** Your exception handler executes, but the caller still receives the exception.

**Cause:** The handler did not call `state.SetHandled(response)`. Without this call, the runtime considers the exception unhandled and continues to the next handler in the hierarchy chain, then to exception actions, then rethrows.

**Fix:** Call `state.SetHandled(...)` with a recovery response inside your handler:

```csharp
public Task Handle(
    GetOrderQuery request,
    TimeoutException exception,
    RequestExceptionHandlerState<OrderDto> state,
    CancellationToken cancellationToken)
{
    state.SetHandled(OrderDto.Empty);

    return Task.CompletedTask;
}
```

### Exception actions do not run

**Symptom:** You implemented `IRequestExceptionAction<TRequest, TException>`, but the action never executes.

**Cause:** Exception actions only run when no exception handler marked the exception as handled. If an `IRequestExceptionHandler` called `state.SetHandled(...)` for the same exception, actions are skipped entirely.

**Fix:** This is by design. The execution flow is:

1. Exception handlers run (most-specific to least-specific). If any calls `state.SetHandled(...)`, the recovery response is returned immediately. Actions do not run.
2. If no handler suppressed the exception, all registered exception actions execute.
3. The original exception is rethrown with its stack trace preserved.

If you need side-effects to run regardless of whether the exception is handled, place that logic inside the exception handler itself, not in an exception action.

## Notification Issues

### Notification handlers are not called

**Symptom:** You call `Publish(notification)` and it completes without error, but none of your notification handlers execute.

**Cause:** `Publish` succeeds silently when no handlers are registered for the notification type. This is by design -- notifications are one-to-many, and zero is a valid "many."

**Fix:**

1. Verify each handler class implements `INotificationHandler<T>` with the correct notification type.
2. Verify the handler classes are in a scanned assembly.
3. Verify the notification type matches exactly. Publishing `CityDeleted` does not invoke handlers registered for `INotificationHandler<CityUpdated>`.

### Notifications are not running in parallel

**Symptom:** Notification handlers execute sequentially, even though you expect concurrent execution.

**Cause:** The default notification publisher strategy is `ForeachAwaitPublisher`, which awaits each handler one at a time in sequence.

**Fix:** Switch to the parallel publisher strategy:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<MyHandler>();
    options.UseNotificationPublisherStrategy(
        NerdigyMediatorNotificationPublisherStrategy.Parallel);
});
```

`TaskWhenAllPublisher` starts all handlers concurrently and awaits `Task.WhenAll`. Be aware that if any handler throws, the exception propagates as an `AggregateException` containing all failures.

### One notification handler failure stops the rest (sequential mode)

**Symptom:** Using the default `ForeachAwaitPublisher`, if an early notification handler throws, subsequent handlers do not execute.

**Cause:** `ForeachAwaitPublisher` awaits handlers in sequence. An unhandled exception from any handler stops the loop and propagates immediately.

**Fix:** Options include:

- Handle exceptions within each notification handler so they do not propagate.
- Switch to `TaskWhenAllPublisher`, which starts all handlers before awaiting any, so a single failure does not prevent other handlers from executing.
- Implement a custom `INotificationPublisher` with your own resilience strategy.

## Streaming Issues

### Stream cancellation is not observed

**Symptom:** Cancelling the `CancellationToken` does not stop stream enumeration.

**Cause:** The stream handler does not check or propagate the cancellation token.

**Fix:** Apply `[EnumeratorCancellation]` to the `CancellationToken` parameter in your handler and check for cancellation during enumeration:

```csharp
public async IAsyncEnumerable<EventEnvelope> Handle(
    ReadEventsRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    foreach (EventEnvelope item in GetEvents(request.StreamId))
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return item;
        await Task.Yield();
    }
}
```

The runtime observes two cancellation tokens:

- The **request token** passed to `CreateStream(request, cancellationToken)`, which flows to the handler and pipeline.
- The **enumeration token** from `WithCancellation(token)`, which is observed during async enumeration.

If both tokens are cancelable, the runtime links them so that either cancellation stops the stream.

### Stream behavior does not execute

**Symptom:** You registered an `IStreamPipelineBehavior<TRequest, TResponse>`, but it does not run for `CreateStream` calls.

**Cause:** Stream requests use `IStreamPipelineBehavior<,>`, not `IPipelineBehavior<,>`. These are separate interfaces. A standard `IPipelineBehavior` does not apply to stream requests.

**Fix:** Implement `IStreamPipelineBehavior<TRequest, TResponse>` for stream-specific middleware:

```csharp
public sealed class StreamLoggingBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    public IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Stream started: {typeof(TRequest).Name}");

        return next();
    }
}
```

::: info
Pre-processors (`IRequestPreProcessor<TRequest>`) run for both standard requests and stream requests. Post-processors (`IRequestPostProcessor<TRequest, TResponse>`) run only for standard requests.
:::

## Debugging Tips

### Verify registrations at startup

Inspect the DI container after calling `AddMediator` to confirm your handlers and pipeline components were registered. Build the service provider and resolve the types you expect:

```csharp
ServiceProvider provider = services.BuildServiceProvider();

// Check that a handler is registered
IRequestHandler<GetOrderQuery, OrderDto>? handler =
    provider.GetService<IRequestHandler<GetOrderQuery, OrderDto>>();

if (handler is null)
{
    Console.WriteLine("GetOrderHandler is NOT registered");
}

// Check notification handlers
IEnumerable<INotificationHandler<OrderCreated>> notificationHandlers =
    provider.GetServices<INotificationHandler<OrderCreated>>();

Console.WriteLine($"OrderCreated handlers: {notificationHandlers.Count()}");

// Check pipeline behaviors
IEnumerable<IPipelineBehavior<GetOrderQuery, OrderDto>> behaviors =
    provider.GetServices<IPipelineBehavior<GetOrderQuery, OrderDto>>();

Console.WriteLine($"GetOrderQuery behaviors: {behaviors.Count()}");
```

::: warning
Use this technique in development and tests only. Do not build a second service provider in production.
:::

### Narrow the problem with a minimal reproduction

If a handler or behavior is not executing as expected:

1. Create a new test project referencing `Nerdigy.Mediator.DependencyInjection`.
2. Register only the request, handler, and the specific component in question.
3. Send the request and verify the result.

This isolates whether the problem is in registration, type matching, or application logic.

### Check type parameter alignment

The most common source of "not found" or "not executing" issues is a mismatch between generic type parameters. When debugging, print the full type names:

```csharp
Console.WriteLine(typeof(IRequestHandler<GetOrderQuery, OrderDto>).FullName);
```

Compare this against what the scanner would construct from your handler class. The service type and implementation type must agree on every generic argument.

## Frequently Asked Questions

### Can I register a handler manually instead of using assembly scanning?

Yes. Register your handler directly with the DI container before or after calling `AddMediator`. Because the scanner uses `TryAdd`, a manually registered handler takes precedence if it is added first:

```csharp
services.AddTransient<IRequestHandler<GetOrderQuery, OrderDto>, GetOrderHandler>();

services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<OtherHandler>();
});
```

### Can I call AddMediator multiple times?

Yes. Core service registrations (`IMediator`, `ISender`, `IPublisher`) use `TryAdd`, so the first call wins and subsequent calls do not produce duplicates. Handler scanning runs for each call, but `TryAdd` and `TryAddEnumerable` prevent duplicate registrations.

### What lifetime should I use for handlers?

The default lifetime is `Transient`. If your handlers inject scoped services (such as `DbContext`), set `HandlerLifetime` to `Scoped`:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<MyHandler>();
    options.HandlerLifetime = ServiceLifetime.Scoped;
});
```

Resolving a scoped dependency from a transient handler can produce subtle lifetime bugs depending on your DI container configuration.

### Why does IRequest extend IRequest\<Unit\>?

`IRequest` (the void request marker) extends `IRequest<Unit>` so that the void dispatch path can share pipeline infrastructure with the response path internally. This is an implementation detail. When you implement a void handler, use `IRequestHandler<TRequest>` (single type parameter) -- not `IRequestHandler<TRequest, Unit>`.

### How do I run logic before every request regardless of type?

Implement an open-generic pre-processor:

```csharp
public sealed class AuditPreProcessor<TRequest> : IRequestPreProcessor<TRequest>
    where TRequest : IBaseRequest
{
    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Processing: {typeof(TRequest).Name}");

        return Task.CompletedTask;
    }
}
```

Place this class in a scanned assembly. The scanner registers it as an open generic, and the DI container closes it at resolve time for every request type.
