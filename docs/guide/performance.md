# Performance

Nerdigy.Mediator dispatches requests, notifications, and streams without per-call reflection and without expression trees. Each concrete message type gets one typed invoker, cached in a `ConcurrentDictionary`, and every call after the first is one dictionary lookup followed by ordinary generic code.

Later calls do not use reflection, but they are not free. Each call looks up the invoker, resolves services from the service provider, and composes the pipeline. Streams also add work for each item. The [Cold Start vs. Warm Path](#cold-start-vs-warm-path) section lists this work.

## How Dispatch Works

The mediator accepts requests through interfaces like `IRequest<TResponse>`. The concrete type of each request is only known at runtime, so the runtime must bridge from the interface to the closed generic handler. It does so with a typed invoker per request type:

```csharp
// Conceptually, for CreateOrder : IRequest<OrderResult>
sealed class RequestInvoker<CreateOrder, OrderResult> : RequestInvoker<OrderResult>
{
    public override Task<OrderResult> Invoke(IRequest<OrderResult> request, IServiceProvider services, CancellationToken ct)
        => RequestPipeline<CreateOrder, OrderResult>.Execute(services, (CreateOrder)request, ResolveAndCallHandler, ct);
}
```

1. **Look up the invoker.** `Send` reads `request.GetType()` and looks up `RequestInvoker<TResponse>` in a static `ConcurrentDictionary`. This is the only lookup on the path.
2. **Create it once if needed.** An invoker registered ahead of time by explicit registration (`AddRequestHandler` and friends, through `MediatorRegistry`) is used as is. Otherwise the first call creates it with `MakeGenericType` and `Activator.CreateInstance`.
3. **Run typed code.** The invoker casts the request once and runs `RequestPipeline<TRequest, TResponse>`, which resolves the components and the handler with their closed generic types. There is no `MethodInfo.Invoke`, no `DynamicInvoke`, and no compiled delegate.

Streams use `StreamInvoker<TResponse>` and notifications `NotificationInvoker` the same way. Notifications only need the lookup when they are published through a less specific static type, such as `INotification`.

### The Fast Path

When a request has no pre-processors, behaviors, or post-processors, the pipeline calls the handler directly. When the handler (or behavior chain) returns a task that already completed successfully, that task is returned to the caller as is: no async state machine, no extra `Task` allocation. Failures and pending tasks take a separate async path that runs exception processing.

## Cold Start vs. Warm Path

The first time a given message type is dispatched without being registered ahead of time, the runtime pays a one-time cost: one `MakeGenericType` and one `Activator.CreateInstance` to create the invoker. There is no expression compilation. With explicit registration the invoker is created at startup instead.

The first time an exception type is processed for a request type, the runtime creates one small invoker per level of the exception's type hierarchy, the same way.

The warm path is every call after the first. It does no reflection, but it does more work than a direct call to the handler.

For each `Send` call, the mediator does this work:

- One `ConcurrentDictionary` lookup, keyed by `request.GetType()`.
- Three service provider resolutions for the pipeline: `IEnumerable<IRequestPreProcessor<TRequest>>`, `IEnumerable<IPipelineBehavior<TRequest, TResponse>>`, and `IEnumerable<IRequestPostProcessor<TRequest, TResponse>>`. This work occurs also when no pipeline components are registered.
- One service provider resolution for the handler instance. The handler lifetime controls whether this resolution creates a new instance.
- With behaviors: one small pipeline context, plus one link object and one `next` delegate per behavior.
- An async method only when the pipeline has pre-processors or post-processors, or the handler does not complete synchronously.

When telemetry is enabled, each operation also runs inside an activity and records metrics. When nothing listens, the cost is one listener check. See [Observability](/guide/observability).

For each `Publish` call, the mediator resolves `IEnumerable<INotificationHandler<TNotification>>` from the service provider. Then it gives the handlers to the configured publisher. If the static type of the notification is sealed, a value type, or the same as its runtime type, no dictionary lookup occurs.

For each `CreateStream` call, the mediator does the same lookup and pipeline resolutions as `Send`, but without post-processors. It also creates the async iterators that wrap the handler stream. If both cancellation tokens can be canceled, it creates a linked `CancellationTokenSource`.

For each stream item, the mediator does this work:

- The item moves through two compiler-generated async iterators. One iterator controls setup, cancellation, and telemetry. The other iterator applies stream exception handling.
- A move-next helper calls `MoveNextAsync` on the handler enumerator inside a `try`/`catch` block. The helper returns a `ValueTask`, so a synchronous `MoveNextAsync` result does not allocate a `Task`.

The benchmarks in this guide compare each operation with a direct call to the handler. Use them to see the warm-path cost on your machine.

## Measured Results

These numbers come from BenchmarkDotNet 0.15.8 `--job short` runs on .NET 10, Linux x64, on an AMD Ryzen AI Max+ 395. They show relative cost; run the benchmarks on your own hardware before relying on absolute numbers.

### Compared with other mediators

From `bench/Nerdigy.Mediator.Benchmarks.Comparison`. Each library resolves transient handlers from a scope and runs the same pass-through behavior.

| Operation | Nerdigy.Mediator 2.0 | Nerdigy.Mediator 1.0 | MediatR 14.2 | martinothamar/Mediator 3.0 (source generated) |
|---|---|---|---|---|
| `Send`, no pipeline | 38.6 ns / 96 B | 113 ns / 456 B | 37.7 ns / 200 B | 20.9 ns / 88 B |
| `Send` through one behavior | 59.1 ns / 312 B | 134 ns / 616 B | 72.3 ns / 440 B | 34.1 ns / 240 B |
| `Publish` to four handlers | 51.1 ns / 184 B | 48 ns / 184 B | 254 ns / 1488 B | 67.6 ns / 152 B |

The 1.0 column was measured with the same benchmark during the 2.0 review. The source-generated mediator stays ahead on `Send` because it has no runtime type lookup and no container resolution of pipeline components.

### Overhead against a direct call

From `bench/Nerdigy.Mediator.Benchmarks`. This project resolves the mediator from the root provider with scope validation on, which adds container cost to every resolution, so its numbers are higher than the comparison above. `SendResolveAndCallDirect` resolves the handler and calls it without the mediator, which shows how much of the cost is the container.

| Benchmark | Mean | Allocated |
|---|---|---|
| `SendDirect` (handler call only) | 4.7 ns | 72 B |
| `SendResolveAndCallDirect` | 22.2 ns | 96 B |
| `Send` | 58.6 ns | 96 B |
| `SendVoid` | 49.4 ns | 24 B |
| `SendValueResponse` | 58.5 ns | 96 B |
| `SendWithPipeline` (one behavior and one preprocessor) | 102 ns | 440 B |
| `SendWithHandledException` | 1.57 µs | 1264 B |
| `Publish` to four handlers | 59.9 ns | 184 B |

Throwing and catching the exception dominates `SendWithHandledException`; avoid exceptions for expected outcomes on hot paths.

### Cold start

`ColdStartBenchmarks` runs each benchmark once in a fresh process, so it includes JIT compilation of the container and the mediator. Scanning one assembly, building the container, and sending the first request took 21.7 ms; with explicit registration instead of scanning it took 18.9 ms. Most of that is the runtime and the container starting up, not dispatch.

## Approach Comparison

Mediator implementations generally choose one of three dispatch strategies. Each makes different tradeoffs:

**Reflection per call.** The simplest approach: resolve the handler type, call `MethodInfo.Invoke` on every dispatch. Straightforward to implement, but `Invoke` allocates an `object[]` for parameters and boxes value-type returns. This cost compounds in high-throughput scenarios.

**Source generators.** Generate dispatch code at compile time. This eliminates runtime type resolution and produces the fastest dispatch. The tradeoff is build-time complexity: generators must handle incremental compilation, IDE integration, diagnostics, and every edge case in the type system. They also cannot dispatch types discovered at runtime.

**Cached generic invokers (Nerdigy.Mediator).** A middle path. Each message type is dispatched through a closed generic invoker, created once at startup or on first use and looked up by type on every call. The code that runs is ordinary generic code the JIT and the Native AOT compiler optimize like any other. It works with any message type discoverable at runtime and needs no build-time tooling. A source generator still avoids the per-call type lookup and some allocations, so it remains faster on the most minimal paths; see the comparison benchmarks below.

## Notification Publisher Strategies

The choice between `ForeachAwaitPublisher` and `TaskWhenAllPublisher` directly affects notification throughput.

**`ForeachAwaitPublisher` (default)** awaits each handler sequentially. Total wall-clock time equals the sum of all handler durations. Choose this when handlers must observe a consistent order or when handler side-effects depend on previous handlers completing first.

**`TaskWhenAllPublisher`** reads the handler sequence, starts all handlers concurrently, and awaits the group. Total wall-clock time equals the duration of the slowest handler. This publisher also includes fast paths for zero-handler and single-handler cases that avoid `Task.WhenAll` overhead entirely. Choose this when handlers are independent and I/O-bound.

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<MyHandler>();

    // Switch to parallel notification dispatch
    options.UseNotificationPublishStrategy(NotificationPublishStrategy.Parallel);
});
```

## Performance Tuning Guidance

### Keep Pipelines Lean

Every pre-processor, behavior, and post-processor runs on every dispatch for its registered request type. The mediator resolves the pre-processor, post-processor, and behavior collections from the service provider on each call. Each behavior also adds one link object, one delegate, and one delegate invocation. Measure the overhead with the `SendWithPipeline` benchmark before you add cross-cutting behaviors.

- Prefer targeted behaviors (closed registrations for specific request types, or open generics constrained to a marker interface) over open-generic behaviors that run on all requests.
- Pre-processors and post-processors force the async path. Prefer a behavior when the work can complete synchronously.
- Avoid I/O in pre-processors and post-processors unless strictly necessary. Logging and metrics are reasonable; database calls are not.

### Minimize Handler Allocations

The benchmark handlers return `Task.FromResult` and `Task.CompletedTask` to avoid async state machine allocations. Apply the same pattern in production handlers that can complete synchronously, so the mediator's fast path can return the handler's task directly:

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
| `SendResolveAndCallDirect` | Send | Resolves the handler from the container and calls it, without the mediator. Separates container cost from mediator cost. |
| `Send` | Send | Request/response dispatch with an empty pipeline. |
| `SendVoid` | Send | Void request dispatch with an empty pipeline. |
| `SendValueResponse` | Send | Dispatch of a request with a value-type (`int`) response and an empty pipeline. |
| `SendWithPipeline` | Send | Request/response dispatch through one open-generic pipeline behavior and one pre-processor. |
| `PublishDirect` | Publish | Baseline. Awaits four notification handler instances in sequence. |
| `Publish` | Publish | Notification dispatch to four handlers with the default `ForeachAwaitPublisher`. |
| `CreateStreamDirect` | Stream | Baseline. Enumerates eight values from a handler that yields synchronously. |
| `CreateStream` | Stream | Stream dispatch and enumeration of the same synchronous handler. This benchmark shows the overhead for each item. |
| `CreateStreamTaskYieldDirect` | StreamTaskYield | Baseline. Enumerates eight values from a handler that calls `Task.Yield` after each value. |
| `CreateStreamTaskYield` | StreamTaskYield | Stream dispatch and enumeration of the `Task.Yield` handler. The scheduler cost is large in this benchmark, so the mediator overhead is a smaller part of the total. |

`AddMediator` registers all benchmark components through assembly scanning. The pipeline behavior has a constraint on a marker interface, so it applies only to the `SendWithPipeline` request. The other benchmarks keep an empty pipeline. `GlobalSetup` makes sure of this and stops the run if the plain request has pipeline components.

### Comparison With Other Mediators

`bench/Nerdigy.Mediator.Benchmarks.Comparison` runs the same three scenarios -- send, send through one pass-through behavior, and publish to four handlers -- against [MediatR](https://github.com/LuckyPennySoftware/MediatR) and [martinothamar/Mediator](https://github.com/martinothamar/Mediator). Every library resolves transient handlers from a scope. martinothamar/Mediator generates its dispatch code at compile time and returns `ValueTask`; MediatR and Nerdigy.Mediator dispatch at runtime and return `Task`. It is a separate project so the main benchmarks do not depend on other mediator packages.

```bash
dotnet run -c Release --project bench/Nerdigy.Mediator.Benchmarks.Comparison -- --filter '*' --job short
```

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
