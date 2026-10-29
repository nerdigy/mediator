# Mediator

`Nerdigy.Mediator` is a mediator runtime for .NET, with support for request/response, notifications, and async streaming workflows. It dispatches without per-call reflection, supports Native AOT, and reports traces and metrics for OpenTelemetry.

Upgrading from 1.x? See the [changelog](CHANGELOG.md) and [Migrating to 2.0](docs/guide/migrating-to-v2.md).

## Packages In This Repository

- `Nerdigy.Mediator`: core mediator runtime, dispatch, pipelines, publishers, and telemetry
- `Nerdigy.Mediator.Contracts`: public contracts and delegate types
- `Nerdigy.Mediator.DependencyInjection`: DI registration (assembly scanning or explicit), startup validation, and options

All projects currently target `net10.0`.

## Quick Start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Nerdigy.Mediator.Contracts;
using Nerdigy.Mediator.DependencyInjection;

var services = new ServiceCollection();

services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<PingHandler>();
});

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

var response = await mediator.Send(new PingQuery("hello"));
Console.WriteLine(response);

public sealed record PingQuery(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<PingQuery, string>
{
    public Task<string> Handle(PingQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"PONG: {request.Message}");
    }
}
```

### Explicit Registration and Native AOT

Instead of scanning, register each component explicitly. Explicit registration is required for trimmed and Native AOT apps, and a Native AOT app registered this way publishes without trim or AOT warnings:

```csharp
services.AddMediator(options => options
    .AddRequestHandler<PingQuery, string, PingHandler>()
    .AddOpenBehavior(typeof(LoggingBehavior<,>)));
```

See [Native AOT](docs/guide/native-aot.md).

### Telemetry

Every send, publish, and stream enumeration is traced and measured through the `Nerdigy.Mediator` activity source and meter:

```csharp
// using Nerdigy.Mediator;
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(MediatorTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(MediatorTelemetry.MeterName));
```

See [Observability](docs/guide/observability.md).

## Repository Layout

- `src/Nerdigy.Mediator.Contracts`
- `src/Nerdigy.Mediator`
- `src/Nerdigy.Mediator.DependencyInjection`
- `test/Nerdigy.Mediator.UnitTests`
- `test/Nerdigy.Mediator.IntegrationTests` (with fixtures in `test/Nerdigy.Mediator.IntegrationTests.Fixtures`)
- `test/Nerdigy.Mediator.AotSmokeTests` (Native AOT smoke test, published and run in CI)
- `bench/Nerdigy.Mediator.Benchmarks`
- `bench/Nerdigy.Mediator.Benchmarks.Comparison` (comparison with MediatR and martinothamar/Mediator)
- `docs/` (VitePress documentation site)

## Common Commands

Run from repository root:

```bash
dotnet build Mediator.slnx
dotnet test Mediator.slnx
dotnet format --verify-no-changes
dotnet run -c Release --project bench/Nerdigy.Mediator.Benchmarks/Nerdigy.Mediator.Benchmarks.csproj
dotnet publish test/Nerdigy.Mediator.AotSmokeTests -c Release -r linux-x64 -o artifacts/aot && ./artifacts/aot/Nerdigy.Mediator.AotSmokeTests
```

Docs site:

```bash
pnpm docs:dev
pnpm docs:build
pnpm docs:preview
```

## What Is Implemented

- Request/response and void-request dispatch (`Send`)
- Notification fan-out (`Publish`) with sequential and parallel publisher strategies
- Stream request dispatch (`CreateStream`)
- Request and stream pipeline behaviors
- Request and stream exception handlers, plus exception actions
- Assembly scanning and explicit registration, with startup validation of duplicate and unreachable components
- Native AOT and trimming support with explicit registration
- Tracing and metrics through `System.Diagnostics` (`Nerdigy.Mediator` activity source and meter)

## Documentation

- [Getting Started](docs/guide/getting-started.md)
- [Dependency Injection](docs/guide/dependency-injection.md)
- [Requests](docs/guide/requests.md)
- [Notifications](docs/guide/notifications.md)
- [Streaming](docs/guide/streaming.md)
- [Pipelines And Processors](docs/guide/pipelines.md)
- [Exception Handling](docs/guide/exception-handling.md)
- [Performance](docs/guide/performance.md)
- [Native AOT](docs/guide/native-aot.md)
- [Observability](docs/guide/observability.md)
- [Migrating to 2.0](docs/guide/migrating-to-v2.md)
- [Troubleshooting](docs/guide/troubleshooting.md)
- [Changelog](CHANGELOG.md)

## Contributing Notes

- Follow `.editorconfig` and namespace-to-path alignment (`namespace == path`).
- Keep changes small and single-purpose.
- Maintain independent implementation practices and do not copy source from external projects.

## License

[MIT](LICENSE)
