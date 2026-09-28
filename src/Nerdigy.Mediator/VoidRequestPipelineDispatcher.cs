using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Dispatches void-style requests through the request pipeline.
/// </summary>
internal static class VoidRequestPipelineDispatcher
{
    private static readonly ConcurrentDictionary<Type, VoidRequestPipelineDispatchDelegate> s_dispatchers = new();

    /// <summary>
    /// Dispatches a void-style request through the configured request pipeline.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that completes when request handling finishes.</returns>
    public static Task Dispatch(IServiceProvider serviceProvider, IRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var dispatcher = s_dispatchers.GetOrAdd(requestType, static type => BuildDispatcher(type));

        return dispatcher(serviceProvider, request, cancellationToken);
    }

    /// <summary>
    /// Gets the typed dispatch method closed over a concrete void-style request type.
    /// </summary>
    /// <remarks>
    /// The returned method accepts the request as <c>IRequest&lt;Unit&gt;</c> and returns <c>Task&lt;Unit&gt;</c>, so it can
    /// also serve <c>Send&lt;Unit&gt;</c> calls for void-style requests.
    /// </remarks>
    /// <param name="requestType">The concrete request runtime type, which must implement <see cref="IRequest"/>.</param>
    /// <returns>The closed dispatch method.</returns>
    internal static MethodInfo GetDispatchMethod(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        var dispatchMethod = typeof(VoidRequestPipelineDispatcher)
            .GetMethod(nameof(DispatchTyped), BindingFlags.NonPublic | BindingFlags.Static);

        if (dispatchMethod is null)
        {
            throw new InvalidOperationException(
                MediatorDiagnostics.MissingDispatchMethod(typeof(VoidRequestPipelineDispatcher), nameof(DispatchTyped)));
        }

        return dispatchMethod.MakeGenericMethod(requestType);
    }

    /// <summary>
    /// Builds a dispatch delegate for a concrete request runtime type.
    /// </summary>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <returns>A compiled dispatch delegate.</returns>
    private static VoidRequestPipelineDispatchDelegate BuildDispatcher(Type requestType)
    {
        var closedDispatchMethod = GetDispatchMethod(requestType);
        var serviceProviderParameter = Expression.Parameter(typeof(IServiceProvider), "serviceProvider");
        var requestParameter = Expression.Parameter(typeof(IRequest), "request");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var dispatchCall = Expression.Call(closedDispatchMethod, serviceProviderParameter, requestParameter, cancellationTokenParameter);

        return Expression.Lambda<VoidRequestPipelineDispatchDelegate>(
            dispatchCall,
            serviceProviderParameter,
            requestParameter,
            cancellationTokenParameter).Compile();
    }

    /// <summary>
    /// Dispatches a void-style request through the pipeline for a concrete request type.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to <see cref="Unit.Value"/> when request handling finishes.</returns>
    private static Task<Unit> DispatchTyped<TRequest>(
        IServiceProvider serviceProvider,
        IRequest<Unit> request,
        CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        var typedRequest = (TRequest)request;

        return RequestPipelineExecutor<TRequest, Unit>.Execute(
            serviceProvider,
            typedRequest,
            cancellationToken,
            async () =>
            {
                await VoidRequestDispatcher.Dispatch(serviceProvider, typedRequest, cancellationToken).ConfigureAwait(false);

                return Unit.Value;
            });
    }

    /// <summary>
    /// Represents a cached void request pipeline dispatch delegate.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that completes when request handling finishes.</returns>
    internal delegate Task VoidRequestPipelineDispatchDelegate(
        IServiceProvider serviceProvider,
        IRequest request,
        CancellationToken cancellationToken);
}