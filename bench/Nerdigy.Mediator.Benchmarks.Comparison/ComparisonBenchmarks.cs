using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

using Microsoft.Extensions.DependencyInjection;

using NerdigyRegistration = Nerdigy.Mediator.DependencyInjection.ServiceCollectionExtensions;

namespace Nerdigy.Mediator.Benchmarks.Comparison;

/// <summary>
/// Compares send, send through one behavior, and publish to four handlers across three mediator libraries.
/// </summary>
/// <remarks>
/// Every library resolves transient handlers from a scope and runs the same pass-through behavior. martinothamar/Mediator
/// generates its dispatch code at compile time and returns <see cref="ValueTask"/>; MediatR and Nerdigy.Mediator dispatch
/// at runtime and return <see cref="Task"/>. MediatR 13 and later log a license warning without a license key, which does
/// not affect the measurements.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ComparisonBenchmarks
{
    private readonly Nerd.Ping _nerdigyPing = new("x");
    private readonly Nerd.PipedPing _nerdigyPipedPing = new("x");
    private readonly Nerd.Note _nerdigyNote = new("x");
    private readonly MediatRTypes.Ping _mediatRPing = new("x");
    private readonly MediatRTypes.PipedPing _mediatRPipedPing = new("x");
    private readonly MediatRTypes.Note _mediatRNote = new("x");
    private readonly Mart.Ping _martPing = new("x");
    private readonly Mart.PipedPing _martPipedPing = new("x");
    private readonly Mart.Note _martNote = new("x");
    private IServiceScope _nerdigyScope = null!;
    private IServiceScope _mediatRScope = null!;
    private IServiceScope _martScope = null!;
    private Nerdigy.Mediator.Contracts.IMediator _nerdigy = null!;
    private MediatR.IMediator _mediatR = null!;
    private global::Mediator.IMediator _mart = null!;

    /// <summary>
    /// Builds one container per library.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var nerdigyServices = new ServiceCollection();
        NerdigyRegistration.AddMediator(nerdigyServices, options => options.RegisterServicesFromAssemblyContaining<ComparisonBenchmarks>());
        _nerdigyScope = nerdigyServices.BuildServiceProvider().CreateScope();
        _nerdigy = _nerdigyScope.ServiceProvider.GetRequiredService<Nerdigy.Mediator.Contracts.IMediator>();

        var mediatRServices = new ServiceCollection();
        mediatRServices.AddLogging();
        mediatRServices.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<ComparisonBenchmarks>();
            configuration.AddOpenBehavior(typeof(MediatRTypes.PassThrough<,>));
        });
        _mediatRScope = mediatRServices.BuildServiceProvider().CreateScope();
        _mediatR = _mediatRScope.ServiceProvider.GetRequiredService<MediatR.IMediator>();

        var martServices = new ServiceCollection();
        MediatorDependencyInjectionExtensions.AddMediator(martServices, (global::Mediator.MediatorOptions options) =>
        {
            options.ServiceLifetime = ServiceLifetime.Transient;
            options.PipelineBehaviors = [typeof(Mart.PassThrough<,>)];
        });
        _martScope = martServices.BuildServiceProvider().CreateScope();
        _mart = _martScope.ServiceProvider.GetRequiredService<global::Mediator.IMediator>();
    }

    /// <summary>
    /// Disposes the scopes.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _nerdigyScope.Dispose();
        _mediatRScope.Dispose();
        _martScope.Dispose();
    }

    /// <summary>Nerdigy.Mediator send with no pipeline.</summary>
    /// <returns>The response.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Send")]
    public Task<string> NerdigySend() => _nerdigy.Send(_nerdigyPing);

    /// <summary>MediatR send with no pipeline.</summary>
    /// <returns>The response.</returns>
    [Benchmark]
    [BenchmarkCategory("Send")]
    public Task<string> MediatRSend() => _mediatR.Send(_mediatRPing);

    /// <summary>martinothamar/Mediator send with no pipeline.</summary>
    /// <returns>The response.</returns>
    [Benchmark]
    [BenchmarkCategory("Send")]
    public ValueTask<string> MartinothamarSend() => _mart.Send(_martPing);

    /// <summary>Nerdigy.Mediator send through one behavior.</summary>
    /// <returns>The response.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SendWithBehavior")]
    public Task<string> NerdigySendWithBehavior() => _nerdigy.Send(_nerdigyPipedPing);

    /// <summary>MediatR send through one behavior.</summary>
    /// <returns>The response.</returns>
    [Benchmark]
    [BenchmarkCategory("SendWithBehavior")]
    public Task<string> MediatRSendWithBehavior() => _mediatR.Send(_mediatRPipedPing);

    /// <summary>martinothamar/Mediator send through one behavior.</summary>
    /// <returns>The response.</returns>
    [Benchmark]
    [BenchmarkCategory("SendWithBehavior")]
    public ValueTask<string> MartinothamarSendWithBehavior() => _mart.Send(_martPipedPing);

    /// <summary>Nerdigy.Mediator publish to four handlers.</summary>
    /// <returns>A task that completes when publishing finishes.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Publish")]
    public Task NerdigyPublish() => _nerdigy.Publish(_nerdigyNote);

    /// <summary>MediatR publish to four handlers.</summary>
    /// <returns>A task that completes when publishing finishes.</returns>
    [Benchmark]
    [BenchmarkCategory("Publish")]
    public Task MediatRPublish() => _mediatR.Publish(_mediatRNote);

    /// <summary>martinothamar/Mediator publish to four handlers.</summary>
    /// <returns>A task that completes when publishing finishes.</returns>
    [Benchmark]
    [BenchmarkCategory("Publish")]
    public ValueTask MartinothamarPublish() => _mart.Publish(_martNote);
}

/// <summary>
/// Runs the comparison benchmarks.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point. Forwards command-line arguments to BenchmarkDotNet.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}