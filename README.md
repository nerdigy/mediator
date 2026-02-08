# Mediator

`Nerdigy.Mediator` is a mediator runtime for .NET, with support for request/response, notifications, and async streaming workflows.

## Packages In This Repository

- `Nerdigy.Mediator.Abstractions`: public contracts and delegate types
- `Nerdigy.Mediator`: core mediator runtime, dispatch, pipelines, and publishers
- `Nerdigy.Mediator.DependencyInjection`: DI registration, assembly scanning, and options

All projects currently target `net10.0`.

## Quick Start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Nerdigy.Mediator.Abstractions;
using Nerdigy.Mediator.DependencyInjection;

var services = new ServiceCollection();

services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<PingHandler>();
});

await using var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();

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

## Repository Layout

- `src/Nerdigy.Mediator.Abstractions`
- `src/Nerdigy.Mediator`
- `src/Nerdigy.Mediator.DependencyInjection`
- `test/Nerdigy.Mediator.UnitTests`
- `test/Nerdigy.Mediator.IntegrationTests`
- `bench/Nerdigy.Mediator.Benchmarks`
- `docs/` (VitePress documentation site)

## Common Commands

Run from repository root:

```bash
dotnet build Mediator.slnx
dotnet test Mediator.slnx
dotnet format --verify-no-changes
dotnet run -c Release --project bench/Nerdigy.Mediator.Benchmarks/Nerdigy.Mediator.Benchmarks.csproj
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
- Assembly scanning for handlers, processors, behaviors, and exception components

## Documentation

- [Getting Started](docs/guide/getting-started.md)
- [Dependency Injection](docs/guide/dependency-injection.md)
- [Requests](docs/guide/requests.md)
- [Notifications](docs/guide/notifications.md)
- [Streaming](docs/guide/streaming.md)
- [Pipelines And Processors](docs/guide/pipelines.md)
- [Exception Handling](docs/guide/exception-handling.md)
- [Performance](docs/guide/performance.md)
- [Troubleshooting](docs/guide/troubleshooting.md)

## Contributing Notes

- Follow `.editorconfig` and namespace-to-path alignment (`namespace == path`).
- Keep changes small and single-purpose.
- Maintain independent implementation practices and do not copy source from external projects.

## License

[MIT](LICENSE)
