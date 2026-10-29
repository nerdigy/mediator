# Migrating to 2.0

2.0 rebuilds dispatch for speed and Native AOT, fixes exception-processing bugs, and makes registration mistakes fail at startup instead of at runtime. Most apps need only the renames in step 1; the other steps apply if you used the affected feature. The full list of changes is in the [changelog](https://github.com/nerdigy/mediator/blob/main/CHANGELOG.md).

## 1. Rename the Options Types

| 1.x | 2.0 |
| --- | --- |
| `NerdigyMediatorOptions` | `MediatorOptions` |
| `NerdigyMediatorNotificationPublisherStrategy` | `NotificationPublishStrategy` |
| `UseNotificationPublisherStrategy(...)` | `UseNotificationPublishStrategy(...)` |

```csharp
// 1.x
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<Program>();
    options.UseNotificationPublisherStrategy(NerdigyMediatorNotificationPublisherStrategy.Parallel);
});

// 2.0
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<Program>();
    options.UseNotificationPublishStrategy(NotificationPublishStrategy.Parallel);
});
```

## 2. Send Requests With Their Declared Response Type

Covariant response views are removed. `IRequest<TResponse>` and `IStreamRequest<TResponse>` are no longer covariant, so a request that declares `IRequest<string>` cannot be sent as `IRequest<object>`.

```csharp
// 1.x
IRequest<object> query = new GetUserNameQuery(42); // declares IRequest<string>
object name = await mediator.Send(query);

// 2.0
string name = await mediator.Send(new GetUserNameQuery(42));
```

If you dispatch requests generically, keep the response type generic too, for example `Task<TResponse> Dispatch<TResponse>(IRequest<TResponse> request)`.

Variance annotations are also removed from every handler and pipeline interface. The container never applied them.

## 3. Fix What AddMediator Now Reports

Run the app. `AddMediator` now throws one `InvalidOperationException` that lists every registration that silently misbehaved in 1.x:

| Problem | What 1.x did | Fix |
| --- | --- | --- |
| Two handlers for the same request | Ran whichever was found first | Keep one, or register the one you want with `AddRequestHandler` to override the scanned one |
| A component closed over an interface or abstract message type, such as `IPipelineBehavior<IAuditable, Unit>` or `INotificationHandler<INotification>` | Registered it and never ran it | Declare it as an open generic constrained to that type |
| `IRequestHandler<TRequest, Unit>` for a request that implements `IRequest` | Registered it and never ran it | Implement `IRequestHandler<TRequest>` |
| A scanned assembly with types that fail to load | Scanned the rest silently | Fix the dependency, or set `options.IgnoreTypeLoadFailures = true` |
| A singleton mediator with scoped components | Failed on the first request with scope validation, or shared one scoped instance without it | Use a `Transient` or `Scoped` mediator |
| A later `AddMediator` call with a different lifetime or publisher | Ignored the later settings | Set them in the first call only |

```csharp
// 1.x: registered, never ran
public sealed class AuditBehavior : IPipelineBehavior<IAuditable, Unit> { /* ... */ }

// 2.0: runs for every request that implements IAuditable
public sealed class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IAuditable
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}
```

Scanned behaviors now run in order of full type name rather than reflection order. If their order matters, register them with `AddBehavior` or `AddOpenBehavior` in the order you want.

## 4. Check Exception Components That Matched Several Levels

Each exception handler or action implementation now runs once per exception, at the most specific level of the exception's type hierarchy it matches. An open generic `GlobalExceptionAction<TRequest, TException>` used to run once for `InvalidOperationException`, again for `SystemException`, and again for `Exception`; it now runs once.

If you relied on one class running at several levels, give each level its own class.

## 5. Handle Cancellation at the Call Site

Exception handlers no longer run when the caller's token was canceled and the request failed with `OperationCanceledException`, so a catch-all handler can no longer turn cancellation into a response. Exception actions still run.

```csharp
try
{
    return await mediator.Send(new GetUserQuery(id), cancellationToken);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // The caller canceled; no exception handler will recover this.
    throw;
}
```

A cancellation the caller did not request, such as a behavior's timeout, can still be recovered.

## 6. Expect MediatorExceptionProcessingException

When an exception handler, an exception action, or cleanup of a failed stream throws, 1.x let that secondary exception replace the original. 2.0 runs every remaining action and throws `MediatorExceptionProcessingException`, with the original as `InnerException` and the secondary failures in `ProcessingExceptions`.

```csharp
catch (MediatorExceptionProcessingException exception) when (exception.InnerException is NotFoundException)
{
    // The request failed with NotFoundException, and an exception component also failed.
}
```

## 7. Pass the Token Through Behaviors (Optional)

`RequestHandlerDelegate<TResponse>` and `StreamHandlerDelegate<TResponse>` now take an optional `CancellationToken`. Existing `next()` calls compile unchanged and keep the current token. Pass a token to give the rest of the pipeline a different one:

```csharp
// 1.x: the handler always received the caller's token
return await next();

// 2.0: the handler receives the linked timeout token
using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeout.CancelAfter(TimeSpan.FromSeconds(5));
return await next(timeout.Token);
```

Code that creates these delegates itself, for example in tests, must accept the parameter: `RequestHandlerDelegate<string> next = _ => Task.FromResult("done");`.

## Other Behavior Changes

- **Stream recovery** disposes the failed enumerator before the exception handler creates a replacement. Recovery happens once per enumeration.
- **`TaskWhenAllPublisher`** reads the whole handler sequence before starting any handler, and returns a faulted task instead of throwing if reading it fails.
- **`AddMediator()` without scanning** is allowed, for handlers registered directly in the service collection.
- **`UseNotificationPublisher<TPublisher>(lifetime)`** takes the publisher's lifetime; it defaults to `Singleton`.

## New in 2.0

- [Explicit registration](/guide/dependency-injection#explicit-registration) and [Native AOT](/guide/native-aot) support.
- [Tracing and metrics](/guide/observability) through the `Nerdigy.Mediator` activity source and meter.
- A faster dispatch path. See [Performance](/guide/performance).
