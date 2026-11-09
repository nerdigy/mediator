# AGENTS.md

Nerdigy.Mediator is a .NET 10 mediator library published as three NuGet packages.

## Commands

Run from the repository root. These are the CI steps in `.github/workflows/ci.yml`, in order, without the coverage and logging flags. Each `--no-build` command needs the Release build.

```bash
dotnet restore Mediator.slnx
dotnet build Mediator.slnx --no-restore -c Release
dotnet format Mediator.slnx --verify-no-changes --no-restore
dotnet test test/Nerdigy.Mediator.UnitTests/Nerdigy.Mediator.UnitTests.csproj --no-build -c Release
dotnet test test/Nerdigy.Mediator.IntegrationTests/Nerdigy.Mediator.IntegrationTests.csproj --no-build -c Release
```

CI then runs both test projects on the .NET 11 runtime, and packs:

```bash
DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 dotnet test test/Nerdigy.Mediator.UnitTests/Nerdigy.Mediator.UnitTests.csproj --no-build -c Release
DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 dotnet test test/Nerdigy.Mediator.IntegrationTests/Nerdigy.Mediator.IntegrationTests.csproj --no-build -c Release
dotnet pack Mediator.slnx --no-build -c Release -o artifacts/nuget
```

A second CI job publishes the Native AOT smoke test and runs it:

```bash
dotnet publish test/Nerdigy.Mediator.AotSmokeTests/Nerdigy.Mediator.AotSmokeTests.csproj -c Release -r linux-x64 -o artifacts/aot
./artifacts/aot/Nerdigy.Mediator.AotSmokeTests
```

Docs site, for a change under `docs/`:

```bash
pnpm install --frozen-lockfile
pnpm docs:build
```

Benchmark commands and results are in `docs/guide/performance.md`.

## Layout

| Path | Contents |
| --- | --- |
| `src/Nerdigy.Mediator.Contracts` | Public contracts and delegates |
| `src/Nerdigy.Mediator` | Runtime: dispatch, pipelines, publishers, telemetry, `MediatorRegistry` |
| `src/Nerdigy.Mediator.DependencyInjection` | `AddMediator`, `MediatorOptions`, scanning, explicit registration, startup validation |
| `test/Nerdigy.Mediator.UnitTests` | Unit tests |
| `test/Nerdigy.Mediator.IntegrationTests` | Integration tests, with fixtures in `test/Nerdigy.Mediator.IntegrationTests.Fixtures` |
| `test/Nerdigy.Mediator.AotSmokeTests` | Console program that CI publishes and runs as a native binary |
| `bench/Nerdigy.Mediator.Benchmarks` | BenchmarkDotNet benchmarks |
| `bench/Nerdigy.Mediator.Benchmarks.Comparison` | Comparison with MediatR and martinothamar/Mediator |
| `docs/` | VitePress site. Files in `docs/public/` are served from the site root |

## Conventions

The build and the format check fail on these:

- Warnings are errors (`Directory.Build.props`).
- Every project generates XML documentation, so a public member without a comment fails the build with `CS1591`.
- `dotnet format --verify-no-changes` fails on formatting that differs from `.editorconfig`.

The Comparison benchmark project opts out of the first two, because its source generator emits code these settings do not govern.

Review holds these, and the tools pass without them. `.editorconfig` sets the namespace rules to suggestion severity.

- XML documentation on every type, method, and property in `src/`, including internal and private ones. Private fields carry none.
- Namespace equals path: `src/Nerdigy.Mediator/Foo.cs` is in `Nerdigy.Mediator`.
- File-scoped namespaces.
- `ConfigureAwait(false)` on every `await` in `src/`.

Commit messages use Conventional Commits: `<type>(<optional scope>): <imperative summary>`. Mark a breaking change with `!` and a `BREAKING CHANGE:` footer.

## Tests

- Use xunit and hand-written fakes. The test projects reference no mocking library.
- Mediator telemetry listeners (`ActivityListener`, `MeterListener`) are process-wide. A listener switches `Send` and `Publish` to a different code path for every test running at that moment. A test that attaches one runs in the non-parallel `Telemetry` xunit collection.
- `MediatorRegistry` registrations and the invoker caches are process-wide statics, and a registration lasts for the whole process. A test that needs an unregistered message declares its own message type.

## Decided behavior

The 2.0.0 section of [CHANGELOG.md](CHANGELOG.md) is decided behavior, and every change keeps these rules. The changelog has the full text.

- `IRequest<TResponse>`, `IStreamRequest<TResponse>`, and every handler and pipeline interface are invariant.
- `RequestHandlerDelegate<TResponse>` and `StreamHandlerDelegate<TResponse>` take a `CancellationToken`. `next()` keeps the current token; `next(token)` passes a different one down.
- Exception handlers and actions run once per exception, at the most specific level they match. A generic implementation counts as one implementation.
- Caller cancellation is never recovered by an exception handler. Exception actions still run. A cancellation the caller did not request can still be handled.
- A failure in an exception handler, an exception action, or failed-stream cleanup throws `MediatorExceptionProcessingException`, with the original exception as `InnerException` and the failures in `ProcessingExceptions`. Every action still runs when an earlier one fails.
- A failed stream is disposed before its replacement is created. A stream is replaced at most once per enumeration.
- Registration mistakes fail `AddMediator` with one `InvalidOperationException` that lists every problem: duplicate handlers, components closed over an interface or abstract message type, `IRequestHandler<TRequest, Unit>` for a request that implements `IRequest`, type load failures while scanning, and a singleton mediator combined with scoped components.
- A later `AddMediator` call cannot change the mediator lifetime or the notification publisher. It can add handlers.
- Scanned behaviors run in order of full type name.
- `TaskWhenAllPublisher` reads the whole handler sequence before it starts any handler, and returns a faulted task if reading fails.
- Assembly scanning is not trim-safe. Explicit registration is the Native AOT path. `net10.0` is the only target.
