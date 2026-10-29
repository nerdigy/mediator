# Runtime Behavior

This reference documents the internal dispatch lifecycle, pipeline execution order, caching strategy, and error handling mechanics of the `Nerdigy.Mediator` runtime. Every detail below reflects the actual source code.

## Request Dispatch Lifecycle

When you call `mediator.Send(request)`, the runtime moves through three internal layers:

```
Mediator.Send<TResponse>()
  --> RequestInvoker<TResponse>.Get(request.GetType())     // one cache lookup
    --> RequestInvoker<TRequest, TResponse>.Invoke()
      --> RequestPipeline<TRequest, TResponse>.Execute()   // pipeline, then the handler
```

**Step 1 -- Invoker lookup.** The runtime looks up the typed invoker for the request's concrete runtime type (e.g. `GetUserQuery`) in a `ConcurrentDictionary`. The invoker was either registered ahead of time by explicit registration, or is created on first use (see [Dispatch Caching](#dispatch-caching)).

**Step 2 -- Typed invoker.** `RequestInvoker<TRequest, TResponse>` casts the request to its concrete type once and hands it to the pipeline, together with a cached delegate that resolves and calls the handler.

**Step 3 -- Pipeline.** `RequestPipeline<TRequest, TResponse>` runs the components in this order:

1. **Pre-processors** -- all registered `IRequestPreProcessor<TRequest>` instances run sequentially in registration order.
2. **Pipeline behaviors** -- all registered `IPipelineBehavior<TRequest, TResponse>` instances wrap the handler in an onion-style chain, outermost-first in registration order. Each behavior receives a `RequestHandlerDelegate<TResponse> next` and decides whether to call it.
3. **Handler** -- the single `IRequestHandler<TRequest, TResponse>` is resolved and executes. If no handler is registered, it throws `InvalidOperationException` with a message naming the missing registration.
4. **Post-processors** -- all registered `IRequestPostProcessor<TRequest, TResponse>` instances run sequentially in registration order, inside the innermost behavior, receiving both the request and the response.

If any stage throws, execution transfers to the [exception processing flow](#exception-processing-flow).

When there are no pre-processors, behaviors, or post-processors, the runtime calls the handler directly. When the pipeline completes synchronously, its task is returned as is, without an extra async state machine.

### Pipeline Construction

The behavior chain is built per call. Each behavior receives a `next` delegate bound to the position after it; calling it invokes the next behavior, or the handler and post-processors after the last behavior:

```csharp
// Conceptual chain for one call
Task<TResponse> InvokeAt(int index, CancellationToken token)
{
    if (index == behaviors.Length)
    {
        return HandlerThenPostProcessors(token);
    }

    RequestHandlerDelegate<TResponse> next =
        nextToken => InvokeAt(index + 1, nextToken == default ? token : nextToken);

    return behaviors[index].Handle(request, next, token);
}
```

The first registered behavior becomes the outermost wrapper. A behavior can short-circuit by returning a response without calling `next()`, and can call `next` more than once to run the rest of the pipeline again.

### Cancellation Tokens in the Pipeline

The caller's token reaches pre-processors and the outermost behavior. A behavior that calls `next(token)` gives that token to every inner behavior, the handler, and the post-processors. Calling `next()` or `next(default)` keeps the token the behavior received.

## Void Request Dispatch

`IRequest` (no response payload) extends `IRequest<Unit>`. `Send(IRequest)` dispatches through `Send<Unit>`, which uses `VoidRequestInvoker<TRequest>` for any request that implements `IRequest`:

```
Mediator.Send(IRequest)
  --> Mediator.Send<Unit>()
    --> VoidRequestInvoker<TRequest>.Invoke()
      --> RequestPipeline<TRequest, Unit>.Execute()  // resolves IRequestHandler<TRequest>
```

Void requests participate in the same pre-processor, behavior, post-processor, and exception handling pipeline as response-bearing requests, with `Unit` as the response type. A void request sent through `Send<Unit>` or held as an `IRequest<Unit>` is dispatched the same way, to `IRequestHandler<TRequest>`, and returns `Unit.Value`.

Requests that implement only `IRequest<Unit>` are ordinary response-bearing requests and use `IRequestHandler<TRequest, Unit>`. A request that implements `IRequest` is never dispatched to `IRequestHandler<TRequest, Unit>`; `AddMediator` rejects that registration, and the missing-handler error names the interface to implement.

## Stream Dispatch Lifecycle

When you call `mediator.CreateStream(request)`, the runtime follows a parallel dispatch chain:

```
Mediator.CreateStream<TResponse>()
  --> StreamInvoker<TResponse>.Get(request.GetType())
    --> StreamInvoker<TRequest, TResponse>.Invoke()
      --> StreamRequestPipeline<TRequest, TResponse>.Execute()
```

`CreateStream` returns immediately. Each enumeration of the returned stream runs:

1. **Pre-processors** -- all registered `IRequestPreProcessor<TRequest>` instances, sequentially.
2. **Stream pipeline behaviors** -- all registered `IStreamPipelineBehavior<TRequest, TResponse>` instances, wrapped outermost-first. Each behavior receives a `StreamHandlerDelegate<TResponse> next` that returns `IAsyncEnumerable<TResponse>`.
3. **Stream handler** -- the single `IStreamRequestHandler<TRequest, TResponse>` produces the `IAsyncEnumerable<TResponse>`.

::: info No post-processors for streams
Stream requests do not execute `IRequestPostProcessor`. Post-processing applies only to `Send()` dispatch.
:::

The scope that resolved the mediator must outlive the whole enumeration, because the handler and behaviors are resolved when enumeration starts.

### Cancellation Token Linking

Stream dispatch observes two cancellation tokens:

- **Request token** -- passed to `CreateStream(..., cancellationToken)`, forwarded to pre-processors, behaviors, and the handler.
- **Enumeration token** -- provided via `WithCancellation(...)` on the `IAsyncEnumerable<TResponse>`.

If both tokens are cancelable, the runtime creates a linked `CancellationTokenSource` and uses the linked token throughout enumeration. If only one is cancelable, it uses that token directly. The linked source is disposed when enumeration completes.

### Stream Exception Handling

Stream exception handling applies at two points:

1. **During pipeline construction** -- exceptions thrown by pre-processors or stream behaviors while building the initial stream.
2. **During enumeration** -- exceptions thrown by `GetAsyncEnumerator()`, `MoveNextAsync()`, or `Current` as the consumer iterates the stream.

At both points, the runtime follows the same [exception processing flow](#exception-processing-flow), but uses `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` instead of request exception handlers. A handled stream exception replaces the active stream with the handler's fallback `IAsyncEnumerable<TResponse>`, and enumeration continues from the replacement stream. Items already yielded are not replayed.

When enumeration fails, the runtime disposes the failed enumerator **before** it calls the exception handlers, so a replacement stream can reopen the same resource. If that disposal throws, the runtime does not attempt a replacement: exception actions run, and it throws `MediatorExceptionProcessingException` with the enumeration failure as `InnerException` and the disposal failure in `ProcessingExceptions`.

Exception handlers can replace a stream one time only per enumeration. If the replacement stream also throws, the runtime does not call the exception handlers again. The exception actions run, and the exception propagates to the consumer. This rule prevents an infinite recovery loop.

Exceptions from `DisposeAsync()` on an enumerator that completed, or that the consumer stopped early, do not go through stream exception handlers or exception actions. The runtime propagates them to the consumer.

## Notification Publishing

`Mediator.Publish<TNotification>()` resolves all registered `INotificationHandler<T>` instances for the notification's concrete runtime type from the DI container and passes them to an `INotificationPublisher` strategy. Publishing through an `INotification`, base-class, or generic reference reaches the same handlers as publishing the concrete type directly. When the static type is sealed, a value type, or already the runtime type, no lookup is needed; otherwise a cached `NotificationInvoker<TNotification>` bridges to the concrete type.

Handlers registered only for an interface or base type are not invoked for a derived notification. `AddMediator` rejects handlers closed over an interface or abstract notification type; use a constrained open generic instead (see [Dependency Injection](/guide/dependency-injection#components-for-an-interface-or-base-message-type)).

If no handlers are registered, publish completes immediately as a no-op.

### Built-in Strategies

| Strategy | Class | Behavior |
|---|---|---|
| **Sequential** (default) | `ForeachAwaitPublisher` | Awaits each handler one at a time, in registration order. A handler exception stops subsequent handlers. |
| **Parallel** | `TaskWhenAllPublisher` | Reads the whole handler sequence, then starts all handlers concurrently and awaits `Task.WhenAll`. All handlers execute even if one faults, including a handler that throws synchronously before returning a task; the publish task faults only after every started handler finishes. If reading the handler sequence fails, no handler starts and the returned task is faulted. |

`TaskWhenAllPublisher` includes fast paths: zero handlers return `Task.CompletedTask`, a single handler executes without creating a `Task[]` array. Parallel handlers share the scope they were resolved from.

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

When any stage of request or stream pipeline execution throws, the runtime applies a two-phase exception processing sequence. A missing handler goes through the same flow.

### Phase 1 -- Exception Handlers

If the caller's token is canceled and the exception is an `OperationCanceledException`, this phase is skipped: the caller's cancellation is never turned into a response. For streams, the enumeration's token is used.

Otherwise the runtime walks the exception type hierarchy from **most specific to least specific** by following each type's `BaseType`. For a thrown `FileNotFoundException`, it checks handlers registered for `FileNotFoundException`, then `IOException`, then `SystemException`, then `Exception`. Only types in the thrown exception's actual inheritance chain are checked: `HttpRequestException` derives directly from `Exception`, so an `IOException` handler never sees it.

For each exception type in the hierarchy, it resolves all registered `IRequestExceptionHandler<TRequest, TResponse, TException>` instances (or `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` for streams) and invokes them in sequence. Each implementation runs at most once per exception, at the most specific level it matches. A generic implementation, such as an open generic the container closed at every level, counts as one implementation.

The first handler to call `state.SetHandled(response)` wins. The runtime returns the recovery response immediately and skips all remaining handlers and actions.

```csharp
// Exception handler resolution order for FileNotFoundException:
// 1. IRequestExceptionHandler<TRequest, TResponse, FileNotFoundException>
// 2. IRequestExceptionHandler<TRequest, TResponse, IOException>
// 3. IRequestExceptionHandler<TRequest, TResponse, SystemException>
// 4. IRequestExceptionHandler<TRequest, TResponse, Exception>
```

### Phase 2 -- Exception Actions

If no handler marks the exception as handled, the runtime executes all registered `IRequestExceptionAction<TRequest, TException>` instances. Actions follow the same most-specific-first ordering and the same once-per-implementation rule. Actions are side-effect-only hooks for logging, metrics, or auditing. Every action runs, even when an earlier one throws.

After all actions execute, the runtime rethrows the original exception using `ExceptionDispatchInfo`, preserving the original instance and stack trace.

### Failures During Processing

If an exception handler throws, an exception action throws, resolving them throws, or a failed stream's cleanup throws, the runtime throws `MediatorExceptionProcessingException` after running every action. Its `InnerException` is the original exception and `ProcessingExceptions` lists the failures. A failing exception handler counts as unhandled, so actions still run for the original exception.

### Exception Processing Summary

```
Exception thrown
  |
  +--> Caller canceled? --YES--> skip handlers
  |
  +--> Exception Handlers (most specific type first, each implementation once)
  |      |
  |      +--> state.SetHandled(response) called?
  |             YES --> return recovery response (done)
  |             NO  --> try next handler
  |
  +--> No handler handled it
  |
  +--> Exception Actions (most specific type first, each implementation once, all execute)
  |
  +--> Any handler or action failed?
         YES --> throw MediatorExceptionProcessingException (original as InnerException)
         NO  --> rethrow original exception (stack trace preserved)
```

## Dispatch Caching

The runtime keeps one typed invoker per concrete message type and dispatches through it, with no expression trees and no per-call reflection.

### How It Works

1. **Registered ahead of time** -- the explicit registration methods (`AddRequestHandler`, `AddStreamRequestHandler`, `AddNotificationHandler`) call `MediatorRegistry`, which stores a statically created invoker for the message type. This is what makes dispatch work under Native AOT.
2. **Created on first use** -- for a message type that was not registered, the first call creates the closed invoker with `MakeGenericType` and `Activator.CreateInstance`. This needs runtime code generation; without it, as under Native AOT, the runtime throws an `InvalidOperationException` naming the registration to add.
3. **Subsequent calls** -- a `ConcurrentDictionary` lookup keyed by `request.GetType()` returns the invoker. The factory may run more than once if several threads hit the same new type at the same time; one result is kept.

### What Gets Cached

| Cache | Keyed By | Stores |
|---|---|---|
| `RequestInvoker<TResponse>` | Concrete request type | `RequestInvoker<TRequest, TResponse>`, or `VoidRequestInvoker<TRequest>` for a void request |
| `StreamInvoker<TResponse>` | Concrete stream request type | `StreamInvoker<TRequest, TResponse>` |
| `NotificationInvoker` | Concrete notification type | `NotificationInvoker<TNotification>`, used only when the static type differs from the runtime type |
| `RequestExceptionProcessor<TRequest, TResponse>` | Exception type | One handler invoker per level of the exception hierarchy |
| `StreamRequestExceptionProcessor<TRequest, TResponse>` | Exception type | One stream handler invoker per level of the exception hierarchy |
| `ExceptionActionProcessor<TRequest>` | Exception type | One action invoker per level of the exception hierarchy |

Handlers, behaviors, and other components are never cached. They are resolved from the service provider on every call, so lifetimes and scopes behave as registered.

### Cache Lifetime

All caches use `static` fields on generic types. They live for the lifetime of the process and are never evicted. Invokers are stateless and receive the `IServiceProvider` on each call, so sharing them across `Mediator` instances and containers is safe.

::: warning
Because caches are never evicted, they keep a reference to every message type dispatched. Apps that load and unload message types from collectible `AssemblyLoadContext`s cannot unload those assemblies after dispatching their messages.
:::

## Internal Type Map

The table below maps each public API method to its internal dispatch chain.

| Public Method | Invoker | Pipeline | Handler |
|---|---|---|---|
| `Send<TResponse>(IRequest<TResponse>)` | `RequestInvoker<TRequest, TResponse>` | `RequestPipeline<TRequest, TResponse>` | `IRequestHandler<TRequest, TResponse>` |
| `Send(IRequest)` | `VoidRequestInvoker<TRequest>` | `RequestPipeline<TRequest, Unit>` | `IRequestHandler<TRequest>` |
| `Send<Unit>(IRequest<Unit>)` with a void request | `VoidRequestInvoker<TRequest>` | `RequestPipeline<TRequest, Unit>` | `IRequestHandler<TRequest>` |
| `CreateStream<TResponse>(IStreamRequest<TResponse>)` | `StreamInvoker<TRequest, TResponse>` | `StreamRequestPipeline<TRequest, TResponse>` | `IStreamRequestHandler<TRequest, TResponse>` |
| `Publish<TNotification>(TNotification)` | `NotificationInvoker<TNotification>` (only for a less specific static type) | -- | `INotificationPublisher` strategy |

## Error Conditions

| Condition | Exception | When |
|---|---|---|
| No handler registered for request type | `InvalidOperationException` | At dispatch time, when the DI container returns `null` for the handler service type. Exception actions run first. |
| No handler registered for stream request type | `InvalidOperationException` | When enumeration starts |
| Message type not registered and runtime code generation unavailable (Native AOT) | `InvalidOperationException` | On the first `Send`, `CreateStream`, or `Publish` for that type |
| Exception handler, exception action, or failed-stream cleanup throws | `MediatorExceptionProcessingException` | After every exception action has run |
| `null` request or notification argument | `ArgumentNullException` | Immediately on `Send`, `CreateStream`, or `Publish` call |
| `null` service provider | `ArgumentNullException` | At `Mediator` construction |

Registration problems, such as duplicate handlers, are reported by `AddMediator` at startup. See [Dependency Injection](/guide/dependency-injection#registration-errors).
