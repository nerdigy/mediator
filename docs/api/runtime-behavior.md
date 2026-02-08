# Runtime Behavior

This reference documents the internal dispatch lifecycle, pipeline execution order, caching strategy, and error handling mechanics of the `Nerdigy.Mediator` runtime. Every detail below reflects the actual source code.

## Request Dispatch Lifecycle

When you call `mediator.Send(request)`, the runtime moves through four internal layers:

```
Mediator.Send<TResponse>()
  --> RequestPipelineDispatcher<TResponse>.Dispatch()
    --> RequestPipelineExecutor<TRequest, TResponse>.Execute()
      --> RequestDispatcher<TResponse>.Dispatch()
```

**Step 1 -- Pipeline Dispatcher.** `RequestPipelineDispatcher<TResponse>` determines the concrete runtime type of the request (e.g. `GetUserQuery`) and looks up a cached dispatch delegate in a `ConcurrentDictionary`. On first encounter, it builds and compiles that delegate via expression trees (see [Dispatch Caching](#dispatch-caching)). The compiled delegate casts the request to its concrete type and forwards it into the pipeline executor.

**Step 2 -- Pipeline Executor.** `RequestPipelineExecutor<TRequest, TResponse>` orchestrates the full pipeline in this order:

1. **Pre-processors** -- all registered `IRequestPreProcessor<TRequest>` instances run sequentially in registration order.
2. **Pipeline behaviors** -- all registered `IPipelineBehavior<TRequest, TResponse>` instances wrap the handler in an onion-style chain, outermost-first in registration order. Each behavior receives a `RequestHandlerDelegate<TResponse> next` and decides whether to call it.
3. **Handler** -- the single `IRequestHandler<TRequest, TResponse>` executes.
4. **Post-processors** -- all registered `IRequestPostProcessor<TRequest, TResponse>` instances run sequentially in registration order, receiving both the request and the response.

If any stage throws, execution transfers to the [exception processing flow](#exception-processing-flow).

**Step 3 -- Request Dispatcher.** `RequestDispatcher<TResponse>` resolves the handler from the DI container and invokes it through a cached, expression-compiled delegate. If no handler is registered, it throws `InvalidOperationException` with a diagnostic message naming the missing registration.

### Pipeline Construction

The pipeline executor builds the behavior chain by iterating behaviors in reverse registration order. Each behavior wraps the next delegate:

```csharp
// Conceptual pipeline assembly (from RequestPipelineExecutor)
RequestHandlerDelegate<TResponse> current = async () =>
{
    TResponse response = await handler();

    foreach (IRequestPostProcessor<TRequest, TResponse> postProcessor in postProcessors)
    {
        await postProcessor.Process(request, response, cancellationToken);
    }

    return response;
};

// Wrap behaviors outermost-first
for (int index = behaviors.Count - 1; index >= 0; index--)
{
    IPipelineBehavior<TRequest, TResponse> behavior = behaviors[index];
    RequestHandlerDelegate<TResponse> next = current;
    current = () => behavior.Handle(request, next, cancellationToken);
}
```

The first registered behavior becomes the outermost wrapper. A behavior can short-circuit by returning a response without calling `next()`.

## Void Request Dispatch

`IRequest` (no response payload) extends `IRequest<Unit>`. When you call `mediator.Send(request)` with a void request, the runtime routes through `VoidRequestPipelineDispatcher` instead of the generic `RequestPipelineDispatcher<TResponse>`.

Internally, void dispatch reuses the same `RequestPipelineExecutor<TRequest, Unit>` pipeline. The terminal handler delegate calls `VoidRequestDispatcher`, which resolves `IRequestHandler<TRequest>` (single type parameter overload) and wraps the result as `Unit.Value`:

```
Mediator.Send(IRequest)
  --> VoidRequestPipelineDispatcher.Dispatch()
    --> RequestPipelineExecutor<TRequest, Unit>.Execute()
      --> VoidRequestDispatcher.Dispatch()
```

This means void requests participate in the same pre-processor, behavior, post-processor, and exception handling pipeline as response-bearing requests.

## Stream Dispatch Lifecycle

When you call `mediator.CreateStream(request)`, the runtime follows a parallel dispatch chain:

```
Mediator.CreateStream<TResponse>()
  --> StreamRequestPipelineDispatcher<TResponse>.Dispatch()
    --> StreamRequestPipelineExecutor<TRequest, TResponse>.Execute()
      --> StreamRequestDispatcher<TResponse>.Dispatch()
```

The stream pipeline executor runs:

1. **Pre-processors** -- all registered `IRequestPreProcessor<TRequest>` instances, sequentially.
2. **Stream pipeline behaviors** -- all registered `IStreamPipelineBehavior<TRequest, TResponse>` instances, wrapped outermost-first. Each behavior receives a `StreamHandlerDelegate<TResponse> next` that returns `IAsyncEnumerable<TResponse>`.
3. **Stream handler** -- the single `IStreamRequestHandler<TRequest, TResponse>` produces the `IAsyncEnumerable<TResponse>`.

::: info No post-processors for streams
Stream requests do not execute `IRequestPostProcessor`. Post-processing applies only to `Send()` dispatch.
:::

### Cancellation Token Linking

Stream dispatch observes two cancellation tokens:

- **Request token** -- passed to `CreateStream(..., cancellationToken)`, forwarded to pre-processors, behaviors, and the handler.
- **Enumeration token** -- provided via `WithCancellation(...)` on the `IAsyncEnumerable<TResponse>`.

If both tokens are cancelable, the runtime creates a linked `CancellationTokenSource` and uses the linked token throughout enumeration. If only one is cancelable, it uses that token directly. The linked source is disposed when enumeration completes.

### Stream Exception Handling

Stream exception handling applies at two points:

1. **During pipeline construction** -- exceptions thrown by pre-processors or stream behaviors while building the initial stream.
2. **During enumeration** -- exceptions thrown by `GetAsyncEnumerator()` or `MoveNextAsync()` as the consumer iterates the stream.

At both points, the runtime follows the same [exception processing flow](#exception-processing-flow), but uses `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` instead of request exception handlers. A handled stream exception replaces the active stream with the handler's fallback `IAsyncEnumerable<TResponse>`, and enumeration continues from the replacement stream.

If an exception occurs mid-enumeration and a handler provides a replacement stream, the runtime disposes the faulted enumerator and switches to the new stream seamlessly.

## Notification Publishing

`Mediator.Publish<TNotification>()` resolves all registered `INotificationHandler<TNotification>` instances from the DI container and delegates to an `INotificationPublisher` strategy.

If no handlers are registered, publish completes immediately as a no-op.

### Built-in Strategies

| Strategy | Class | Behavior |
|---|---|---|
| **Sequential** (default) | `ForeachAwaitPublisher` | Awaits each handler one at a time, in registration order. A handler exception stops subsequent handlers. |
| **Parallel** | `TaskWhenAllPublisher` | Starts all handlers concurrently, then awaits `Task.WhenAll`. All handlers execute even if one faults. |

`TaskWhenAllPublisher` includes fast paths: zero handlers return `Task.CompletedTask`, a single handler executes without creating a `Task[]` array.

### Custom Publishers

Implement `INotificationPublisher` to control handler orchestration. The runtime passes the resolved handler collection, notification, and cancellation token to your implementation:

```csharp
public interface INotificationPublisher
{
    Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification;
}
```

::: tip
Notifications do not participate in the request pipeline. There are no pre-processors, post-processors, or behaviors for notifications -- only the publishing strategy controls handler execution.
:::

## Exception Processing Flow

When any stage of request or stream pipeline execution throws, the runtime applies a two-phase exception processing sequence.

### Phase 1 -- Exception Handlers

The runtime walks the exception type hierarchy from **most specific to least specific**. For a thrown `HttpRequestException`, it checks handlers registered for `HttpRequestException`, then `IOException`, then `Exception`, and so on up the inheritance chain.

For each exception type in the hierarchy, it resolves all registered `IRequestExceptionHandler<TRequest, TResponse, TException>` instances (or `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` for streams) and invokes them in sequence.

The first handler to call `state.SetHandled(response)` wins. The runtime returns the recovery response immediately and skips all remaining handlers and actions.

```csharp
// Exception handler resolution order for HttpRequestException:
// 1. IRequestExceptionHandler<TRequest, TResponse, HttpRequestException>
// 2. IRequestExceptionHandler<TRequest, TResponse, IOException>
// 3. IRequestExceptionHandler<TRequest, TResponse, Exception>
```

### Phase 2 -- Exception Actions

If no handler marks the exception as handled, the runtime executes all registered `IRequestExceptionAction<TRequest, TException>` instances. Actions follow the same most-specific-first hierarchy ordering. Actions are side-effect-only hooks for logging, metrics, or auditing.

After all actions execute, the runtime rethrows the original exception using `ExceptionDispatchInfo.Capture(exception).Throw()`, preserving the original stack trace.

### Exception Processing Summary

```
Exception thrown
  |
  +--> Exception Handlers (most specific type first)
  |      |
  |      +--> state.SetHandled(response) called?
  |             YES --> return recovery response (done)
  |             NO  --> try next handler
  |
  +--> No handler handled it
  |
  +--> Exception Actions (most specific type first, all execute)
  |
  +--> Rethrow original exception (stack trace preserved)
```

## Dispatch Caching

Every dispatcher in the runtime (`RequestPipelineDispatcher`, `RequestDispatcher`, `VoidRequestPipelineDispatcher`, `VoidRequestDispatcher`, `StreamRequestPipelineDispatcher`, `StreamRequestDispatcher`) uses the same caching pattern to eliminate per-call reflection.

### How It Works

1. **First call for a concrete request type** -- the dispatcher uses `System.Linq.Expressions` to build an expression tree that casts the request and handler to their concrete types and calls the appropriate method. It compiles this expression into a delegate.
2. **Cache storage** -- the compiled delegate is stored in a `static ConcurrentDictionary<Type, TDelegate>` keyed by the concrete request runtime type.
3. **Subsequent calls** -- `ConcurrentDictionary.GetOrAdd` returns the cached delegate. No reflection occurs.

This applies at two levels per dispatch:

- **Pipeline dispatch delegates** -- map `IRequest<TResponse>` to the closed `DispatchTyped<TRequest>` method.
- **Handler invocation delegates** -- map a resolved `object` handler to a strongly-typed `Handle` method call with the correct cast.

### What Gets Cached

| Cache | Keyed By | Stores |
|---|---|---|
| `RequestPipelineDispatcher<TResponse>` | Concrete request type | Compiled delegate calling `DispatchTyped<TRequest>` |
| `RequestDispatcher<TResponse>` | Concrete request type | Compiled handler invoker + service resolution |
| `VoidRequestPipelineDispatcher` | Concrete request type | Compiled delegate calling `DispatchTyped<TRequest>` |
| `VoidRequestDispatcher` | Concrete request type | Compiled handler invoker + service resolution |
| `StreamRequestPipelineDispatcher<TResponse>` | Concrete request type | Compiled delegate calling `DispatchTyped<TRequest>` |
| `StreamRequestDispatcher<TResponse>` | Concrete request type | Compiled handler invoker + service resolution |

Exception handler and action invokers are also cached per exception type in `ConcurrentDictionary` instances on `RequestExceptionProcessor` and `StreamRequestExceptionProcessor`.

### Cache Lifetime

All caches use `static` fields on generic types. They live for the lifetime of the application domain and are never evicted. This is safe because the set of concrete request types in an application is fixed at compile time.

::: warning
Because caches are static, they are shared across all `Mediator` instances in the same process. This is intentional and correct -- the compiled delegates are stateless and receive the `IServiceProvider` as a parameter on each call.
:::

## Internal Type Map

The table below maps each public API method to its internal dispatch chain.

| Public Method | Pipeline Dispatcher | Pipeline Executor | Handler Dispatcher |
|---|---|---|---|
| `Send<TResponse>(IRequest<TResponse>)` | `RequestPipelineDispatcher<TResponse>` | `RequestPipelineExecutor<TRequest, TResponse>` | `RequestDispatcher<TResponse>` |
| `Send(IRequest)` | `VoidRequestPipelineDispatcher` | `RequestPipelineExecutor<TRequest, Unit>` | `VoidRequestDispatcher` |
| `CreateStream<TResponse>(IStreamRequest<TResponse>)` | `StreamRequestPipelineDispatcher<TResponse>` | `StreamRequestPipelineExecutor<TRequest, TResponse>` | `StreamRequestDispatcher<TResponse>` |
| `Publish<TNotification>(TNotification)` | -- | -- | `INotificationPublisher` strategy |

## Error Conditions

| Condition | Exception | When |
|---|---|---|
| No handler registered for request type | `InvalidOperationException` | At dispatch time, when the DI container returns `null` for the handler service type |
| No handler registered for stream request type | `InvalidOperationException` | At dispatch time (same as above) |
| `null` request or notification argument | `ArgumentNullException` | Immediately on `Send`, `CreateStream`, or `Publish` call |
| `null` service provider | `ArgumentNullException` | At `Mediator` construction |
