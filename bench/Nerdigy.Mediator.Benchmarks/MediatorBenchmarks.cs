using System.Runtime.CompilerServices;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator.Contracts;
using Nerdigy.Mediator.DependencyInjection;

namespace Nerdigy.Mediator.Benchmarks;

/// <summary>
/// Benchmarks core mediator operations: send, publish, and stream creation.
/// </summary>
/// <remarks>
/// Each category has a direct-handler baseline. The baseline calls the handler instance without the mediator,
/// so the ratio column shows the mediator overhead for that category.
/// </remarks>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class MediatorBenchmarks
{
    private const string SendCategory = "Send";
    private const string PublishCategory = "Publish";
    private const string StreamCategory = "Stream";
    private const string YieldingStreamCategory = "StreamTaskYield";

    private ServiceProvider? _serviceProvider;
    private IMediator? _mediator;
    private PingRequest? _sendRequest;
    private PipelinePingRequest? _pipelineSendRequest;
    private PingNotification? _notification;
    private CountStreamRequest? _streamRequest;
    private YieldingCountStreamRequest? _yieldingStreamRequest;
    private PingRequestHandler? _sendHandler;
    private INotificationHandler<PingNotification>[]? _notificationHandlers;
    private CountStreamRequestHandler? _streamHandler;
    private YieldingCountStreamRequestHandler? _yieldingStreamHandler;

    /// <summary>
    /// Marks requests that the benchmark pipeline behavior applies to.
    /// </summary>
    /// <remarks>
    /// The open-generic behavior constrains its request type to this interface, so requests without it keep an empty pipeline.
    /// </remarks>
    private interface IPipelineBenchmarkRequest;

    /// <summary>
    /// Gets or sets the number of values returned by the stream benchmark handlers.
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

        EnsureBareRequestHasNoPipeline(_serviceProvider);

        _sendRequest = new PingRequest("payload");
        _pipelineSendRequest = new PipelinePingRequest("payload");
        _notification = new PingNotification("payload");
        _streamRequest = new CountStreamRequest(StreamLength);
        _yieldingStreamRequest = new YieldingCountStreamRequest(StreamLength);

        _sendHandler = new PingRequestHandler();
        _notificationHandlers =
        [
            new PingNotificationHandlerOne(),
            new PingNotificationHandlerTwo(),
            new PingNotificationHandlerThree(),
            new PingNotificationHandlerFour()
        ];
        _streamHandler = new CountStreamRequestHandler();
        _yieldingStreamHandler = new YieldingCountStreamRequestHandler();
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
    /// Calls the request handler directly, without the mediator.
    /// </summary>
    /// <returns>A task that resolves to the response payload.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(SendCategory)]
    public Task<string> SendDirect()
    {
        EnsureInitialized();

        return _sendHandler!.Handle(_sendRequest!, CancellationToken.None);
    }

    /// <summary>
    /// Benchmarks request/response dispatch with an empty pipeline.
    /// </summary>
    /// <returns>A task that resolves to the response payload.</returns>
    [Benchmark]
    [BenchmarkCategory(SendCategory)]
    public Task<string> Send()
    {
        EnsureInitialized();

        return _mediator!.Send(_sendRequest!, CancellationToken.None);
    }

    /// <summary>
    /// Benchmarks request/response dispatch through one open-generic behavior and one pre-processor.
    /// </summary>
    /// <returns>A task that resolves to the response payload.</returns>
    [Benchmark]
    [BenchmarkCategory(SendCategory)]
    public Task<string> SendWithPipeline()
    {
        EnsureInitialized();

        return _mediator!.Send(_pipelineSendRequest!, CancellationToken.None);
    }

    /// <summary>
    /// Calls the four notification handlers directly and in sequence, without the mediator.
    /// </summary>
    /// <returns>A task that completes when all handlers finish.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(PublishCategory)]
    public async Task PublishDirect()
    {
        EnsureInitialized();

        foreach (var handler in _notificationHandlers!)
        {
            await handler.Handle(_notification!, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Benchmarks notification publishing to four handlers.
    /// </summary>
    /// <returns>A task that completes when publishing finishes.</returns>
    [Benchmark]
    [BenchmarkCategory(PublishCategory)]
    public Task Publish()
    {
        EnsureInitialized();

        return _mediator!.Publish(_notification!, CancellationToken.None);
    }

    /// <summary>
    /// Enumerates the synchronous stream handler directly, without the mediator.
    /// </summary>
    /// <returns>A task that resolves to the sum of streamed values.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(StreamCategory)]
    public Task<int> CreateStreamDirect()
    {
        EnsureInitialized();

        return SumAsync(_streamHandler!.Handle(_streamRequest!, CancellationToken.None));
    }

    /// <summary>
    /// Benchmarks stream dispatch and full enumeration of a handler that yields synchronously.
    /// </summary>
    /// <returns>A task that resolves to the sum of streamed values.</returns>
    [Benchmark]
    [BenchmarkCategory(StreamCategory)]
    public Task<int> CreateStream()
    {
        EnsureInitialized();

        return SumAsync(_mediator!.CreateStream(_streamRequest!, CancellationToken.None));
    }

    /// <summary>
    /// Enumerates the <see cref="Task.Yield"/> stream handler directly, without the mediator.
    /// </summary>
    /// <returns>A task that resolves to the sum of streamed values.</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory(YieldingStreamCategory)]
    public Task<int> CreateStreamTaskYieldDirect()
    {
        EnsureInitialized();

        return SumAsync(_yieldingStreamHandler!.Handle(_yieldingStreamRequest!, CancellationToken.None));
    }

    /// <summary>
    /// Benchmarks stream dispatch and full enumeration of a handler that calls <see cref="Task.Yield"/> after each value.
    /// </summary>
    /// <returns>A task that resolves to the sum of streamed values.</returns>
    [Benchmark]
    [BenchmarkCategory(YieldingStreamCategory)]
    public Task<int> CreateStreamTaskYield()
    {
        EnsureInitialized();

        return SumAsync(_mediator!.CreateStream(_yieldingStreamRequest!, CancellationToken.None));
    }

    /// <summary>
    /// Enumerates a stream and sums its values.
    /// </summary>
    /// <param name="stream">The stream to enumerate.</param>
    /// <returns>A task that resolves to the sum of streamed values.</returns>
    private static async Task<int> SumAsync(IAsyncEnumerable<int> stream)
    {
        var sum = 0;

        await foreach (var value in stream.ConfigureAwait(false))
        {
            sum += value;
        }

        return sum;
    }

    /// <summary>
    /// Throws when the bare send request resolves pipeline components, which would skew the empty-pipeline benchmark.
    /// </summary>
    /// <param name="serviceProvider">The service provider to inspect.</param>
    private static void EnsureBareRequestHasNoPipeline(IServiceProvider serviceProvider)
    {
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<PingRequest, string>>();
        var preProcessors = serviceProvider.GetServices<IRequestPreProcessor<PingRequest>>();

        if (behaviors.Any() || preProcessors.Any())
        {
            throw new InvalidOperationException("The bare send request must not resolve pipeline behaviors or pre-processors.");
        }
    }

    /// <summary>
    /// Throws when benchmark dependencies were not initialized.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_mediator is null
            || _sendRequest is null
            || _pipelineSendRequest is null
            || _notification is null
            || _streamRequest is null
            || _yieldingStreamRequest is null
            || _sendHandler is null
            || _notificationHandlers is null
            || _streamHandler is null
            || _yieldingStreamHandler is null)
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
    /// Represents a sample request for send benchmarks with a populated pipeline.
    /// </summary>
    /// <param name="Payload">The request payload.</param>
    private sealed record PipelinePingRequest(string Payload) : IRequest<string>, IPipelineBenchmarkRequest;

    /// <summary>
    /// Handles <see cref="PipelinePingRequest"/> requests.
    /// </summary>
    private sealed class PipelinePingRequestHandler : IRequestHandler<PipelinePingRequest, string>
    {
        /// <summary>
        /// Handles a pipeline ping request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<string> Handle(PipelinePingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(request.Payload);
        }
    }

    /// <summary>
    /// Runs before <see cref="PipelinePingRequestHandler"/> and does no work.
    /// </summary>
    private sealed class PipelinePingRequestPreProcessor : IRequestPreProcessor<PipelinePingRequest>
    {
        /// <summary>
        /// Processes the request before the handler runs.
        /// </summary>
        /// <param name="request">The request to process.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(PipelinePingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An open-generic pipeline behavior that calls the next delegate and does no other work.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    private sealed class PassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>, IPipelineBenchmarkRequest
    {
        /// <summary>
        /// Calls the next delegate in the pipeline.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="next">The next delegate in the pipeline.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response from the next delegate.</returns>
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return next();
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
    /// Represents a sample stream request whose handler yields values synchronously.
    /// </summary>
    /// <param name="Count">The number of stream values to emit.</param>
    private sealed record CountStreamRequest(int Count) : IStreamRequest<int>;

    /// <summary>
    /// Handles <see cref="CountStreamRequest"/> requests by producing sequential values without a scheduler hop.
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
        /// Produces sequential values. Every <c>MoveNextAsync</c> call completes synchronously.
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
            }
        }
    }

    /// <summary>
    /// Represents a sample stream request whose handler calls <see cref="Task.Yield"/> after each value.
    /// </summary>
    /// <param name="Count">The number of stream values to emit.</param>
    private sealed record YieldingCountStreamRequest(int Count) : IStreamRequest<int>;

    /// <summary>
    /// Handles <see cref="YieldingCountStreamRequest"/> requests by producing sequential values with a scheduler hop per value.
    /// </summary>
    private sealed class YieldingCountStreamRequestHandler : IStreamRequestHandler<YieldingCountStreamRequest, int>
    {
        /// <summary>
        /// Handles the stream request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of response values.</returns>
        public IAsyncEnumerable<int> Handle(YieldingCountStreamRequest request, CancellationToken cancellationToken)
        {
            return Enumerate(request.Count, cancellationToken);
        }

        /// <summary>
        /// Produces sequential values and yields to the scheduler after each value.
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