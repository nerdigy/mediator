using System.Runtime.CompilerServices;

using BenchmarkDotNet.Attributes;

using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator.Abstractions;
using Nerdigy.Mediator.DependencyInjection;

namespace Nerdigy.Mediator.Benchmarks;

/// <summary>
/// Benchmarks core mediator operations: send, publish, and stream creation.
/// </summary>
[MemoryDiagnoser]
public sealed class MediatorBenchmarks
{
    private ServiceProvider? _serviceProvider;
    private IMediator? _mediator;
    private PingRequest? _sendRequest;
    private PingNotification? _notification;
    private CountStreamRequest? _streamRequest;

    /// <summary>
    /// Gets or sets the number of values returned by the stream benchmark handler.
    /// </summary>
    [Params(8)]
    public int StreamLength { get; set; }

    /// <summary>
    /// Initializes benchmark dependencies and warmup requests.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddMediator(options => options.RegisterServicesFromAssemblyContaining<PingRequestHandler>());

        _serviceProvider = services.BuildServiceProvider(validateScopes: true);
        _mediator = _serviceProvider.GetRequiredService<IMediator>();
        _sendRequest = new PingRequest("payload");
        _notification = new PingNotification("payload");
        _streamRequest = new CountStreamRequest(StreamLength);
    }

    /// <summary>
    /// Releases benchmark dependencies.
    /// </summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _serviceProvider?.Dispose();
    }

    /// <summary>
    /// Benchmarks request/response dispatch.
    /// </summary>
    /// <returns>A task that resolves to the response payload.</returns>
    [Benchmark(Baseline = true)]
    public Task<string> Send()
    {
        EnsureInitialized();

        return _mediator!.Send(_sendRequest!, CancellationToken.None);
    }

    /// <summary>
    /// Benchmarks notification publishing to multiple handlers.
    /// </summary>
    /// <returns>A task that completes when publishing finishes.</returns>
    [Benchmark]
    public Task Publish()
    {
        EnsureInitialized();

        return _mediator!.Publish(_notification!, CancellationToken.None);
    }

    /// <summary>
    /// Benchmarks stream request creation and full enumeration.
    /// </summary>
    /// <returns>A task that resolves to the sum of streamed values.</returns>
    [Benchmark]
    public async Task<int> CreateStream()
    {
        EnsureInitialized();

        var sum = 0;

        await foreach (var value in _mediator!.CreateStream(_streamRequest!, CancellationToken.None))
        {
            sum += value;
        }

        return sum;
    }

    /// <summary>
    /// Throws when benchmark dependencies were not initialized.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_mediator is null || _sendRequest is null || _notification is null || _streamRequest is null)
        {
            throw new InvalidOperationException("Benchmark state is not initialized. Ensure GlobalSetup has run.");
        }
    }

    /// <summary>
    /// Represents a sample request for send benchmarks.
    /// </summary>
    /// <param name="Payload">The request payload.</param>
    private sealed record PingRequest(string Payload) : IRequest<string>;

    /// <summary>
    /// Handles <see cref="PingRequest"/> requests.
    /// </summary>
    private sealed class PingRequestHandler : IRequestHandler<PingRequest, string>
    {
        /// <summary>
        /// Handles a ping request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<string> Handle(PingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(request.Payload);
        }
    }

    /// <summary>
    /// Represents a sample notification for publish benchmarks.
    /// </summary>
    /// <param name="Payload">The notification payload.</param>
    private sealed record PingNotification(string Payload) : INotification;

    /// <summary>
    /// Handles <see cref="PingNotification"/> notifications.
    /// </summary>
    private sealed class PingNotificationHandlerOne : INotificationHandler<PingNotification>
    {
        /// <summary>
        /// Handles the notification.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(PingNotification notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles <see cref="PingNotification"/> notifications.
    /// </summary>
    private sealed class PingNotificationHandlerTwo : INotificationHandler<PingNotification>
    {
        /// <summary>
        /// Handles the notification.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(PingNotification notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles <see cref="PingNotification"/> notifications.
    /// </summary>
    private sealed class PingNotificationHandlerThree : INotificationHandler<PingNotification>
    {
        /// <summary>
        /// Handles the notification.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(PingNotification notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles <see cref="PingNotification"/> notifications.
    /// </summary>
    private sealed class PingNotificationHandlerFour : INotificationHandler<PingNotification>
    {
        /// <summary>
        /// Handles the notification.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(PingNotification notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents a sample stream request for create-stream benchmarks.
    /// </summary>
    /// <param name="Count">The number of stream values to emit.</param>
    private sealed record CountStreamRequest(int Count) : IStreamRequest<int>;

    /// <summary>
    /// Handles <see cref="CountStreamRequest"/> requests by producing sequential values.
    /// </summary>
    private sealed class CountStreamRequestHandler : IStreamRequestHandler<CountStreamRequest, int>
    {
        /// <summary>
        /// Handles the stream request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of response values.</returns>
        public IAsyncEnumerable<int> Handle(CountStreamRequest request, CancellationToken cancellationToken)
        {
            return Enumerate(request.Count, cancellationToken);
        }

        /// <summary>
        /// Produces sequential values.
        /// </summary>
        /// <param name="count">The number of values to produce.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of response values.</returns>
        private static async IAsyncEnumerable<int> Enumerate(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return index;
                await Task.Yield();
            }
        }
    }
}