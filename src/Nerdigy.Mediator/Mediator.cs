using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Default runtime implementation of <see cref="IMediator"/>.
/// </summary>
public sealed class Mediator : IMediator
{
    private readonly INotificationPublisher _notificationPublisher;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class using a sequential notification publisher.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    public Mediator(IServiceProvider serviceProvider)
        : this(serviceProvider, new ForeachAwaitPublisher())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="notificationPublisher">The notification publishing strategy.</param>
    public Mediator(IServiceProvider serviceProvider, INotificationPublisher notificationPublisher)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(notificationPublisher);
        _serviceProvider = serviceProvider;
        _notificationPublisher = notificationPublisher;
    }

    /// <summary>
    /// Sends a request to its single handler and returns the handler response.
    /// </summary>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RequestPipelineDispatcher<TResponse>.Dispatch(_serviceProvider, request, cancellationToken);
    }

    /// <summary>
    /// Sends a request with no response payload to its single handler.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that completes when request handling finishes.</returns>
    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await VoidRequestPipelineDispatcher.Dispatch(_serviceProvider, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a stream request and returns a stream of responses from the single handler.
    /// </summary>
    /// <typeparam name="TResponse">The streamed response payload type.</typeparam>
    /// <param name="request">The stream request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return StreamRequestPipelineDispatcher<TResponse>.Dispatch(_serviceProvider, request, cancellationToken);
    }

    /// <summary>
    /// Publishes a notification to all registered handlers.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var handlers = _serviceProvider.GetService(typeof(IEnumerable<INotificationHandler<TNotification>>))
            as IEnumerable<INotificationHandler<TNotification>>
            ?? [];

        return _notificationPublisher.Publish(handlers, notification, cancellationToken);
    }
}
