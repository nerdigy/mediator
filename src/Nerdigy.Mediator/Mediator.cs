using Nerdigy.Mediator.Contracts;

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

        var invoker = RequestInvoker<TResponse>.Get(request.GetType());

        return MediatorInstrumentation.IsEnabled
            ? SendWithTelemetry(invoker, request, cancellationToken)
            : invoker.Invoke(request, _serviceProvider, cancellationToken);
    }

    /// <summary>
    /// Sends a request with no response payload to its single handler.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that completes when request handling finishes.</returns>
    public Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        return Send<Unit>(request, cancellationToken);
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

        return StreamInvoker<TResponse>.Get(request.GetType()).Invoke(request, _serviceProvider, cancellationToken);
    }

    /// <summary>
    /// Publishes a notification to all handlers registered for its concrete runtime type.
    /// </summary>
    /// <typeparam name="TNotification">The notification type inferred at the call site.</typeparam>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        return MediatorInstrumentation.IsEnabled
            ? PublishWithTelemetry(notification, cancellationToken)
            : NotificationDispatcher.Dispatch(_serviceProvider, _notificationPublisher, notification, cancellationToken);
    }

    /// <summary>
    /// Sends a request inside a telemetry scope.
    /// </summary>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <param name="invoker">The invoker for the request type.</param>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    private async Task<TResponse> SendWithTelemetry<TResponse>(
        RequestInvoker<TResponse> invoker,
        IRequest<TResponse> request,
        CancellationToken cancellationToken)
    {
        using var telemetry = MediatorInstrumentation.Start("send", request.GetType());

        try
        {
            return await invoker.Invoke(request, _serviceProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            telemetry?.Fail(exception, cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Publishes a notification inside a telemetry scope.
    /// </summary>
    /// <typeparam name="TNotification">The notification type inferred at the call site.</typeparam>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    private async Task PublishWithTelemetry<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        using var telemetry = MediatorInstrumentation.Start("publish", notification.GetType());

        try
        {
            await NotificationDispatcher.Dispatch(_serviceProvider, _notificationPublisher, notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            telemetry?.Fail(exception, cancellationToken);

            throw;
        }
    }
}