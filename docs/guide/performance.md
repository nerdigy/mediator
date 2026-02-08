# Performance

Nerdigy.Mediator dispatches requests, notifications, and streams without per-call reflection. Every dispatch path compiles an expression tree once on first use and caches the resulting delegate in a `ConcurrentDictionary`. Subsequent calls execute that compiled delegate directly -- the same cost as a hand-written method call plus a dictionary lookup.

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

On the warm path -- every call after the first -- the cost is:

- `request.GetType()` (single virtual call)
- `ConcurrentDictionary.GetOrAdd` with a cache hit (thread-safe, lock-free read)
- The compiled delegate invocation (two casts and a direct method call)
- Service provider resolution for the handler instance

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

Every pre-processor, behavior, and post-processor runs on every dispatch for its registered request type. Each component adds a delegate invocation and a service-provider resolution. Measure before adding cross-cutting behaviors to ensure the overhead is justified.

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

The benchmark project uses [BenchmarkDotNet](https://benchmarkdotnet.org/) and covers the three core dispatch operations with minimal handlers (no pipelines, no I/O) to isolate mediator overhead from application logic.

| Benchmark | What It Measures |
|---|---|
| `Send` | Request/response dispatch: resolve handler, execute, return `Task<string>` |
| `Publish` | Notification fan-out: resolve four handlers, execute sequentially |
| `CreateStream` | Stream dispatch: resolve handler, enumerate eight values via `IAsyncEnumerable<int>` |

### Running Benchmarks

```bash
dotnet run -c Release --project bench/Nerdigy.Mediator.Benchmarks/Nerdigy.Mediator.Benchmarks.csproj
```

BenchmarkDotNet produces a results table with columns for mean runtime, standard deviation, and heap allocations. Run benchmarks in Release configuration on a quiet machine for consistent results.

### Interpreting Results

- **Compare across commits, not single runs.** Absolute numbers vary by machine. Track relative changes to catch regressions.
- **Focus on the Mean and Allocated columns.** Mean shows throughput changes; Allocated shows allocation regressions.
- **The Send benchmark is the baseline.** BenchmarkDotNet marks it with `[Benchmark(Baseline = true)]`, so Publish and CreateStream ratios are relative to Send.
- **Treat regressions as investigation signals.** A 10% mean increase or any new allocation warrants investigation before release.

::: info
Benchmark results are not committed to the repository because they are machine-specific. Run them locally to establish your baseline.
:::
