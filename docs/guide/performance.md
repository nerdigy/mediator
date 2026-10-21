# Performance

Nerdigy.Mediator dispatches requests, notifications, and streams without per-call reflection. The request and stream paths compile an expression tree once for each request type. The mediator caches the resulting delegate in a `ConcurrentDictionary`.

Later calls do not use reflection, but they are not free. Each call looks up cached delegates, resolves services from the service provider, and composes the pipeline. Streams also add work for each item. The [Cold Start vs. Warm Path](#cold-start-vs-warm-path) section lists this work.

## How Dispatch Caching Works

The mediator accepts requests through interfaces like `IRequest<TResponse>`. At runtime, the concrete type of each request is unknown at compile time, so the dispatcher must bridge the gap between the interface and the closed generic handler. Nerdigy.Mediator solves this with a three-step pattern:

1. **Resolve the concrete type.** On the first call to `Send`, the dispatcher reads the runtime `Type` of the incoming request.

2. **Build and compile an expression tree.** The dispatcher constructs an `Expression.Call` that casts the handler and request to their concrete types and invokes the `Handle` method directly. It compiles this expression into a strongly-typed delegate via `Expression.Lambda<T>(...).Compile()`.

3. **Cache the delegate.** The compiled delegate is stored in a `static ConcurrentDictionary<Type, TDelegate>` keyed by the concrete request type. Every subsequent call for that request type retrieves the cached delegate and invokes it -- no reflection, no `MethodInfo.Invoke`, no `DynamicInvoke`.

This pattern applies uniformly to all six dispatcher types:

| Dispatcher | Cached Delegate |
|---|---|
| `RequestDispatcher<TResponse>` | Handler invoker for `IRequestHandler<TRequest, TResponse>` |
| `VoidRequestDispatcher` | Handler invoker for `IRequestHandler<TRequest>` |
| `StreamRequestDispatcher<TResponse>` | Handler invoker for `IStreamRequestHandler<TRequest, TResponse>` |
| `RequestPipelineDispatcher<TResponse>` | Full pipeline entry point for requests with responses |
| `VoidRequestPipelineDispatcher` | Full pipeline entry point for void requests |
| `StreamRequestPipelineDispatcher<TResponse>` | Full pipeline entry point for stream requests |

The handler-level dispatchers cache a compiled delegate that performs a cast and direct method call. The pipeline-level dispatchers cache a compiled delegate that calls through to the typed `Execute` method on the pipeline executor, which in turn wraps the handler with pre-processors, behaviors, and post-processors.

### What the Compiled Delegate Looks Like

For a request `CreateOrder : IRequest<OrderResult>`, the expression tree compiled by `RequestDispatcher<OrderResult>` is equivalent to:

```csharp
(object handler, IRequest<OrderResult> request, CancellationToken ct) =>
    ((IRequestHandler<CreateOrder, OrderResult>)handler).Handle((CreateOrder)request, ct);
```

Two casts and a direct method call. No dictionary lookups inside the delegate, no `MethodInfo`, no boxing of value-type responses.

## Cold Start vs. Warm Path

The first time a given request type is dispatched, the runtime pays a one-time cost:

- **Type resolution:** `MakeGenericType` to close `IRequestHandler<,>` over the concrete request type.
- **Method lookup:** `GetMethod` to locate the `Handle` method on the closed handler interface.
- **Expression compilation:** `Expression.Lambda<T>(...).Compile()` to produce the cached delegate.

This happens once per request type per application lifetime. The compiled delegate is stored in a `static` field, so it survives across scoped service provider instances.

The warm path is every call after the first. It does no reflection, but it does more work than a direct call to the handler.

For each `Send` call, the mediator does this work:

- Two `ConcurrentDictionary.GetOrAdd` cache hits, keyed by `request.GetType()`. One lookup is for the pipeline delegate. The other lookup is for the handler delegate.
- Three service provider resolutions for the pipeline: `IEnumerable<IRequestPreProcessor<TRequest>>`, `IEnumerable<IRequestPostProcessor<TRequest, TResponse>>`, and `IEnumerable<IPipelineBehavior<TRequest, TResponse>>`. This work occurs also when no pipeline components are registered.
- One service provider resolution for the handler instance. The handler lifetime controls whether this resolution creates a new instance.
- Delegate and closure allocations to compose the pipeline. The mediator allocates one handler delegate, one post-processor wrapper, and one delegate for each behavior.
- An `async` pipeline method that returns a `Task<TResponse>`. A `Task<TResponse>` result is usually a new allocation, also when the handler completes synchronously.

For each `Publish` call, the mediator resolves `IEnumerable<INotificationHandler<TNotification>>` from the service provider. Then it gives the handlers to the configured publisher. If the runtime type of the notification is the same as the static type, no dictionary lookup occurs.

For each `CreateStream` call, the mediator does the same lookups and pipeline resolutions as `Send`, but without post-processors. It also creates the async iterators that wrap the handler stream. If both cancellation tokens can be canceled, it creates a linked `CancellationTokenSource`.

For each stream item, the mediator does this work:

- The item moves through two compiler-generated async iterators. One iterator controls setup and cancellation. The other iterator applies stream exception handling.
- A move-next helper calls `MoveNextAsync` on the handler enumerator inside a `try`/`catch` block. The helper returns a `ValueTask`, so a synchronous `MoveNextAsync` result does not allocate a `Task`.

The benchmarks in this guide compare each operation with a direct call to the handler. Use them to see the warm-path cost on your machine.

::: tip
In web applications, the cold-start cost is typically absorbed during the first HTTP request. Every subsequent request for the same type executes on the warm path.
:::

## Approach Comparison

Mediator implementations generally choose one of three dispatch strategies. Each makes different tradeoffs:

**Reflection per call.** The simplest approach: resolve the handler type, call `MethodInfo.Invoke` on every dispatch. Straightforward to implement, but `Invoke` allocates an `object[]` for parameters and boxes value-type returns. This cost compounds in high-throughput scenarios.

**Source generators.** Generate dispatch code at compile time. This eliminates all runtime type resolution and produces the fastest possible dispatch. The tradeoff is build-time complexity: generators must handle incremental compilation, IDE integration, diagnostics, and every edge case in the type system. They also cannot dispatch types discovered at runtime.

**Expression-tree compilation (Nerdigy.Mediator).** A middle path. The first call per request type pays a one-time compilation cost comparable to source-generator output. Every subsequent call executes a compiled delegate with no reflection overhead. This approach works with any request type discoverable at runtime, requires no build-time tooling, and produces dispatch delegates that the JIT can optimize further.

## Notification Publisher Strategies

The choice between `ForeachAwaitPublisher` and `TaskWhenAllPublisher` directly affects notification throughput.

**`ForeachAwaitPublisher` (default)** awaits each handler sequentially. Total wall-clock time equals the sum of all handler durations. Choose this when handlers must observe a consistent order or when handler side-effects depend on previous handlers completing first.

**`TaskWhenAllPublisher`** starts all handlers concurrently and awaits the group. Total wall-clock time equals the duration of the slowest handler. This publisher also includes fast paths for zero-handler and single-handler cases that avoid `Task.WhenAll` overhead entirely. Choose this when handlers are independent and I/O-bound.

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<MyHandler>();

    // Switch to parallel notification dispatch
    options.UseNotificationPublisherStrategy(
        NerdigyMediatorNotificationPublisherStrategy.Parallel);
});
```

## Performance Tuning Guidance

### Keep Pipelines Lean

Every pre-processor, behavior, and post-processor runs on every dispatch for its registered request type. The mediator resolves the pre-processor, post-processor, and behavior collections from the service provider on each call. Each behavior also adds one closure, one delegate, and one delegate invocation. Measure the overhead with the `SendWithPipeline` benchmark before you add cross-cutting behaviors.

- Prefer targeted behaviors (closed generic registrations for specific request types) over open-generic behaviors that run on all requests.
- Avoid I/O in pre-processors and post-processors unless strictly necessary. Logging and metrics are reasonable; database calls are not.

### Minimize Handler Allocations

The benchmark handlers return `Task.FromResult` and `Task.CompletedTask` to avoid async state machine allocations. Apply the same pattern in production handlers that can complete synchronously:

```csharp
public sealed class LookupHandler : IRequestHandler<LookupQuery, LookupResult>
{
    private readonly ICache _cache;

    public LookupHandler(ICache cache)
    {
        _cache = cache;
    }

    public Task<LookupResult> Handle(LookupQuery request, CancellationToken cancellationToken)
    {
        // Synchronous cache lookup -- no async state machine allocated
        LookupResult result = _cache.Get(request.Key);

        return Task.FromResult(result);
    }
}
```

### Choose the Right Service Lifetime

Handler lifetime affects both allocation rate and correctness. Transient handlers allocate a new instance per dispatch. Scoped handlers share an instance within a DI scope (e.g., a single HTTP request). Singleton handlers allocate once for the application lifetime but must be thread-safe and must not capture scoped dependencies. See the [Dependency Injection](/guide/dependency-injection) guide for configuration details.

## Benchmarks

The benchmark project uses [BenchmarkDotNet](https://benchmarkdotnet.org/). The handlers are minimal and do no I/O, so the results show the mediator overhead. The benchmarks are in categories. Each category has a direct-handler baseline, marked with `[Benchmark(Baseline = true)]`. The baseline calls the handler instance without the mediator and without the service provider.

| Benchmark | Category | What It Measures |
|---|---|---|
| `SendDirect` | Send | Baseline. Calls the request handler instance directly. |
| `Send` | Send | Request/response dispatch with an empty pipeline. |
| `SendWithPipeline` | Send | Request/response dispatch through one open-generic pipeline behavior and one pre-processor. |
| `PublishDirect` | Publish | Baseline. Awaits four notification handler instances in sequence. |
| `Publish` | Publish | Notification dispatch to four handlers with the default `ForeachAwaitPublisher`. |
| `CreateStreamDirect` | Stream | Baseline. Enumerates eight values from a handler that yields synchronously. |
| `CreateStream` | Stream | Stream dispatch and enumeration of the same synchronous handler. This benchmark shows the overhead for each item. |
| `CreateStreamTaskYieldDirect` | StreamTaskYield | Baseline. Enumerates eight values from a handler that calls `Task.Yield` after each value. |
| `CreateStreamTaskYield` | StreamTaskYield | Stream dispatch and enumeration of the `Task.Yield` handler. The scheduler cost is large in this benchmark, so the mediator overhead is a smaller part of the total. |

`AddMediator` registers all benchmark components through assembly scanning. The pipeline behavior has a constraint on a marker interface, so it applies only to the `SendWithPipeline` request. The other benchmarks keep an empty pipeline. `GlobalSetup` makes sure of this and stops the run if the plain request has pipeline components.

### Running Benchmarks

Run all benchmarks:

```bash
dotnet run -c Release --project bench/Nerdigy.Mediator.Benchmarks/Nerdigy.Mediator.Benchmarks.csproj
```

The program sends its command-line arguments to `BenchmarkSwitcher`. Put the BenchmarkDotNet arguments after `--`. For example, run only the Send category with the short job:

```bash
dotnet run -c Release --project bench/Nerdigy.Mediator.Benchmarks -- --filter '*Send*' --job short
```

Show the benchmark names without a run:

```bash
dotnet run -c Release --project bench/Nerdigy.Mediator.Benchmarks -- --list flat
```

BenchmarkDotNet produces a results table with columns for mean runtime, standard deviation, ratio to the baseline, and heap allocations. Run benchmarks in Release configuration on a quiet machine for consistent results.

### Interpreting Results

- **Compare across commits, not single runs.** Absolute numbers vary by machine. Track relative changes to catch regressions.
- **Focus on the Mean and Allocated columns.** Mean shows throughput changes; Allocated shows allocation regressions.
- **Read the ratios in each category.** The results table groups the benchmarks by category. The `Ratio` and `Alloc Ratio` columns compare each benchmark with the direct-handler baseline of its category.
- **Treat regressions as investigation signals.** A 10% mean increase or any new allocation warrants investigation before release.

::: info
Benchmark results are not committed to the repository because they are machine-specific. Run them locally to establish your baseline.
:::
