using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator.Abstractions;
using Nerdigy.Mediator.DependencyInjection;

namespace Nerdigy.Mediator.IntegrationTests;

/// <summary>
/// Verifies dependency injection registration and assembly scanning behavior.
/// </summary>
public sealed class MediatorDependencyInjectionIntegrationTests
{
    /// <summary>
    /// Verifies that configuring mediator services without scan assemblies throws.
    /// </summary>
    [Fact]
    public void AddMediator_WhenNoAssembliesConfigured_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMediator(_ => { }));
        Assert.Contains("No assemblies were configured", exception.Message);
    }

    /// <summary>
    /// Verifies scanned request handlers and pipeline components execute end-to-end.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenScanningAssemblies_ResolvesSendPipelineAndHandlers()
    {
        using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        var response = await mediator.Send(new IntegrationRequest("alpha"), CancellationToken.None);

        Assert.Equal("handled:alpha", response);
        Assert.Contains("pre", tracker.Events);
        Assert.Contains("generic-behavior:before", tracker.Events);
        Assert.Contains("behavior:before", tracker.Events);
        Assert.Contains("handler", tracker.Events);
        Assert.Contains("post:handled:alpha", tracker.Events);
        Assert.Contains("behavior:after", tracker.Events);
        Assert.Contains("generic-behavior:after", tracker.Events);
    }

    /// <summary>
    /// Verifies open-generic behaviors added in options execute in configured order.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenOpenBehaviorsConfigured_ExecutesInConfiguredOrder()
    {
        using var provider = BuildProvider(options =>
        {
            options.AddOpenBehavior(typeof(OrderedIntegrationRequestBehaviorA<,>));
            options.AddOpenBehavior(typeof(OrderedIntegrationRequestBehaviorB<,>));
        });
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        _ = await mediator.Send(new IntegrationRequest("alpha"), CancellationToken.None);

        var events = tracker.Events.ToList();
        Assert.Equal("pre", events[0]);
        Assert.True(events.IndexOf("ordered-a:before") < events.IndexOf("ordered-b:before"));
        Assert.True(events.IndexOf("ordered-a:before") < events.IndexOf("handler"));
        Assert.True(events.IndexOf("ordered-b:before") < events.IndexOf("handler"));
        Assert.True(events.IndexOf("ordered-b:after") > events.IndexOf("handler"));
        Assert.True(events.IndexOf("ordered-a:after") > events.IndexOf("handler"));
        Assert.True(events.IndexOf("ordered-b:after") < events.IndexOf("ordered-a:after"));
    }

    /// <summary>
    /// Verifies non-open behavior registrations through options are rejected.
    /// </summary>
    [Fact]
    public void AddMediator_WhenOpenBehaviorTypeIsNotOpenGeneric_ThrowsArgumentException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddMediator(options =>
            {
                options.RegisterServicesFromAssemblyContaining<IntegrationRequestHandler>();
                options.AddOpenBehavior(typeof(IntegrationRequestBehavior));
            }));

        Assert.Equal("openBehaviorType", exception.ParamName);
    }

    /// <summary>
    /// Verifies scanned request exception handlers execute and recover responses.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenRequestThrows_ResolvesExceptionHandler()
    {
        using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        var response = await mediator.Send(new ThrowingIntegrationRequest("boom"), CancellationToken.None);

        Assert.Equal("recovered", response);
        Assert.Contains("throwing-handler", tracker.Events);
        Assert.Contains("throwing-handler:exception-handled", tracker.Events);
    }

    /// <summary>
    /// Verifies scanned request exception actions execute and exceptions are rethrown when unhandled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenRequestExceptionUnhandled_ExecutesActionAndRethrows()
    {
        using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new UnhandledThrowingIntegrationRequest("boom"), CancellationToken.None));
        Assert.Contains("unhandled-send-action", tracker.Events);
    }

    /// <summary>
    /// Verifies scanned notification handlers are all resolved and invoked.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenPublishingNotification_InvokesAllHandlers()
    {
        using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        await mediator.Publish(new IntegrationNotification("n1"), CancellationToken.None);

        Assert.Equal(2, tracker.NotificationCount);
        Assert.Contains("notification-handler-1", tracker.Events);
        Assert.Contains("notification-handler-2", tracker.Events);
    }

    /// <summary>
    /// Verifies scanned stream handlers and stream pipeline behaviors execute end-to-end.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenCreatingStream_ResolvesStreamPipelineAndHandlers()
    {
        using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        var values = await ToListAsync(mediator.CreateStream(new IntegrationStreamRequest(3), CancellationToken.None), CancellationToken.None);

        Assert.Equal([2, 4, 6], values);
        Assert.Contains("stream-pre", tracker.Events);
        Assert.Contains("generic-stream-behavior:before", tracker.Events);
        Assert.Contains("stream-behavior:before", tracker.Events);
        Assert.Contains("stream-handler:start", tracker.Events);
        Assert.Contains("stream-handler:end", tracker.Events);
        Assert.Contains("stream-behavior:after", tracker.Events);
        Assert.Contains("generic-stream-behavior:after", tracker.Events);
    }

    /// <summary>
    /// Verifies scanned stream exception handlers execute and replace stream output.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenStreamThrows_ResolvesStreamExceptionHandler()
    {
        using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        var values = await ToListAsync(mediator.CreateStream(new ThrowingIntegrationStreamRequest(), CancellationToken.None), CancellationToken.None);

        Assert.Equal([99], values);
        Assert.Contains("stream-exception-handled", tracker.Events);
    }

    /// <summary>
    /// Verifies scanned request exception actions execute for unhandled stream exceptions.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task AddMediator_WhenStreamExceptionUnhandled_ExecutesActionAndRethrows()
    {
        using var provider = BuildProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var tracker = provider.GetRequiredService<IntegrationTracker>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToListAsync(mediator.CreateStream(new UnhandledThrowingIntegrationStreamRequest(), CancellationToken.None), CancellationToken.None));
        Assert.Contains("unhandled-stream-action", tracker.Events);
    }

    /// <summary>
    /// Verifies configuring the parallel publisher strategy registers <see cref="TaskWhenAllPublisher"/>.
    /// </summary>
    [Fact]
    public void AddMediator_WhenParallelPublisherConfigured_RegistersTaskWhenAllPublisher()
    {
        using var provider = BuildProvider(options => options.UseNotificationPublisherStrategy(NerdigyMediatorNotificationPublisherStrategy.Parallel));

        var publisher = provider.GetRequiredService<INotificationPublisher>();

        Assert.IsType<TaskWhenAllPublisher>(publisher);
    }

    /// <summary>
    /// Builds a service provider configured with mediator scanning for integration test types.
    /// </summary>
    /// <param name="configure">Additional mediator option configuration.</param>
    /// <returns>A configured service provider.</returns>
    private static ServiceProvider BuildProvider(Action<NerdigyMediatorOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IntegrationTracker>();
        services.AddMediator(options =>
        {
            options.RegisterServicesFromAssemblyContaining<IntegrationRequestHandler>();
            configure?.Invoke(options);
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// Enumerates an asynchronous sequence and materializes it as a list.
    /// </summary>
    /// <typeparam name="T">The sequence element type.</typeparam>
    /// <param name="source">The sequence to enumerate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to all sequence values.</returns>
    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<T> results = [];

        await foreach (var value in source.WithCancellation(cancellationToken))
        {
            results.Add(value);
        }

        return results;
    }
}