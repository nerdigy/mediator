using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator.Contracts;
using Nerdigy.Mediator.DependencyInjection;

namespace Nerdigy.Mediator.Benchmarks;

/// <summary>
/// Measures the first send in a fresh process: scanning, building the container, and creating the request's dispatch code.
/// </summary>
/// <remarks>
/// Each launch is a new process and runs one iteration, so the dispatch caches start empty every time.
/// </remarks>
[SimpleJob(RunStrategy.ColdStart, launchCount: 10, warmupCount: 0, iterationCount: 1)]
[MemoryDiagnoser]
public class ColdStartBenchmarks
{
    /// <summary>
    /// Scans this assembly, builds the container, and sends the first request.
    /// </summary>
    /// <returns>A task that resolves to the response payload.</returns>
    [Benchmark]
    public async Task<string> ScanBuildAndFirstSend()
    {
        var services = new ServiceCollection();
        services.AddMediator(options => options.RegisterServicesFromAssemblyContaining<ColdStartBenchmarks>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ColdStartRequest("payload"));
    }

    /// <summary>
    /// Registers the handler explicitly, builds the container, and sends the first request.
    /// </summary>
    /// <returns>A task that resolves to the response payload.</returns>
    [Benchmark]
    public async Task<string> ExplicitBuildAndFirstSend()
    {
        var services = new ServiceCollection();
        services.AddMediator(options => options.AddRequestHandler<ColdStartRequest, string, ColdStartRequestHandler>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ColdStartRequest("payload"));
    }

    /// <summary>
    /// Represents the request sent by the cold start benchmarks.
    /// </summary>
    /// <param name="Payload">The request payload.</param>
    public sealed record ColdStartRequest(string Payload) : IRequest<string>;

    /// <summary>
    /// Handles <see cref="ColdStartRequest"/> requests.
    /// </summary>
    public sealed class ColdStartRequestHandler : IRequestHandler<ColdStartRequest, string>
    {
        /// <inheritdoc />
        public Task<string> Handle(ColdStartRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.Payload);
        }
    }
}