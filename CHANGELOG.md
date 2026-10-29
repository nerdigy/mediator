# Changelog

All notable changes to the Nerdigy.Mediator packages are documented here. The project follows
[Semantic Versioning](https://semver.org/).

## 2.0.0

2.0 rebuilds dispatch for speed and Native AOT, fixes exception-processing bugs, and makes registration mistakes fail at
startup instead of at runtime. It contains breaking changes; see [Migrating from 1.x](#migrating-from-1x).

### Breaking changes

- **Covariant response views are removed.** `IRequest<TResponse>` and `IStreamRequest<TResponse>` are no longer
  covariant, so `Send((IRequest<object>)request)` no longer compiles. Send requests with the response type they declare.
- **Variance annotations are removed from every handler and pipeline interface.** The container never honored them:
  a component closed over an interface or abstract message type registered without error and then never ran.
- **`RequestHandlerDelegate<TResponse>` and `StreamHandlerDelegate<TResponse>` take a `CancellationToken`.** Behaviors
  call `next()` as before, or `next(token)` to give the rest of the pipeline a different token, such as one linked to a
  timeout. Behaviors that implement the delegate themselves must accept the parameter.
- **Options types are renamed.** `NerdigyMediatorOptions` is now `MediatorOptions`, and
  `NerdigyMediatorNotificationPublisherStrategy` is now `NotificationPublishStrategy`. `UseNotificationPublisherStrategy`
  is now `UseNotificationPublishStrategy`.
- **Exception components run once per exception.** Exception handlers and actions are still resolved from the most
  specific exception type to `Exception`, but each implementation now runs only at the most specific level it matches,
  like a `catch` block. An open generic `LogAll<TRequest, TException>` action used to run once per level of the
  hierarchy.
- **Caller cancellation is never recovered.** When the caller's token is canceled and the request fails with
  `OperationCanceledException`, exception handlers are skipped and the cancellation propagates. Exception actions still
  run. A cancellation the caller did not request, such as a behavior's timeout, can still be handled.
- **A failure during exception processing no longer hides the original exception.** When an exception handler, an
  exception action, or cleanup of a failed stream throws, the mediator throws `MediatorExceptionProcessingException`
  with the original exception as `InnerException` and the processing failures in `ProcessingExceptions`. Every exception
  action still runs when an earlier one fails.
- **A failed stream is disposed before recovery.** A stream exception handler's replacement stream is now created
  after the failed enumerator is disposed, so it can reopen the same resource.
- **Registration mistakes fail `AddMediator`.** One `InvalidOperationException` lists every problem:
  - two different handlers for the same request, whether scanned, explicit, or from separate `AddMediator` calls;
  - components closed over an interface or abstract message type, such as `IPipelineBehavior<IAuditable, Unit>` or
    `INotificationHandler<INotification>` (use a constrained open generic instead);
  - `IRequestHandler<TRequest, Unit>` for a request that implements `IRequest` (implement `IRequestHandler<TRequest>`);
  - scanned assemblies with types that fail to load (set `MediatorOptions.IgnoreTypeLoadFailures` to scan the rest);
  - a singleton mediator combined with scoped handlers, behaviors, or publisher.
- **A later `AddMediator` call cannot change the mediator lifetime or notification publisher** set by the first call.
  It can still add handlers, so modules can each call `AddMediator`.
- **Scanned behaviors run in order of full type name**, not in the order reflection returns types.
- **`TaskWhenAllPublisher` reads the whole handler sequence before starting any handler**, and returns a faulted task
  instead of throwing if reading fails.

### Added

- **Explicit registration**, for trimmed and Native AOT apps or for anyone who prefers it to scanning:
  `AddRequestHandler` (request and void request), `AddStreamRequestHandler`, `AddNotificationHandler`, `AddBehavior`,
  `AddStreamBehavior`, `AddOpenStreamBehavior`, `AddPreProcessor`, `AddPostProcessor`, `AddExceptionHandler`,
  `AddExceptionAction`, and `AddStreamExceptionHandler`. Each takes an optional lifetime. An explicit handler replaces a
  scanned handler for the same request.
- **`AddMediator()` without scanning**, for handlers registered directly in the service collection.
- **Native AOT and trimming support.** All three packages are annotated and analyzer-clean. With explicit registration,
  a Native AOT app publishes without trim or AOT warnings and dispatches class and struct requests, void requests,
  streams, notifications (including through `INotification`), and exception hierarchies.
- **`MediatorRegistry`**, which registers dispatch code for message types when you construct `Mediator` yourself under
  Native AOT. The explicit registration methods call it for you.
- **Tracing and metrics.** The `Nerdigy.Mediator` activity source emits `Nerdigy.Mediator.Send`,
  `Nerdigy.Mediator.Publish`, and `Nerdigy.Mediator.Stream` activities. A stream's activity covers its whole
  enumeration. The `Nerdigy.Mediator` meter reports `nerdigy.mediator.operation.duration`,
  `nerdigy.mediator.operation.active`, and `nerdigy.mediator.publish.handlers`. Names are constants on
  `MediatorTelemetry`. Nothing is recorded, and the hot path pays only a listener check, until something listens.
- **`UseNotificationPublisher<TPublisher>(lifetime)`** takes the publisher's lifetime.
- **Benchmarks** for void requests, value-type responses, and resolving the handler from the container without the
  mediator, plus a comparison project against MediatR and martinothamar/Mediator.

### Changed

- **Faster dispatch.** Each request type is dispatched through one cached, typed invoker: no expression trees, no
  second lookup, and no async state machine when the request has no pipeline components and the handler completes
  synchronously.
- **Packaging.** Each package ships its own README, release notes link, and package validation. Builds are
  deterministic, and CI builds set `ContinuousIntegrationBuild`.
- **CI** runs the tests on the .NET 11 runtime, publishes and runs a Native AOT smoke test, uploads coverage, and packs
  on every pull request. Releases use NuGet trusted publishing, and the manual release version input can no longer
  inject shell commands.

### Migrating from 1.x

1. Rename `NerdigyMediatorOptions` to `MediatorOptions`, `NerdigyMediatorNotificationPublisherStrategy` to
   `NotificationPublishStrategy`, and `UseNotificationPublisherStrategy` to `UseNotificationPublishStrategy`.
2. Replace covariant sends such as `Send((IRequest<object>)request)` with sends through the declared response type.
3. Run the app. `AddMediator` now reports duplicate handlers and components that never ran; fix each one as its message
   describes.
4. If you relied on an exception handler or action running at several levels of an exception hierarchy, give each level
   its own class.
5. If you relied on a catch-all exception handler swallowing cancellation, handle `OperationCanceledException` at the
   call site instead.
6. If you catch exceptions thrown by `Send`, also expect `MediatorExceptionProcessingException` when an exception
   component fails; the original exception is its `InnerException`.
7. Optionally, pass the token through behaviors with `next(cancellationToken)`.