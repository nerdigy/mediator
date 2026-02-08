using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Executes request processing pipelines for a concrete request and response pair.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal static class RequestPipelineExecutor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Executes preprocessors, pipeline behaviors, handler logic, postprocessors, and exception policies.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed during execution.</param>
    /// <param name="handler">The terminal handler delegate.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public static async Task<TResponse> Execute(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken,
        RequestHandlerDelegate<TResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);

        try
        {
            await ExecutePreProcessors(serviceProvider, request, cancellationToken).ConfigureAwait(false);

            var pipeline = BuildPipeline(serviceProvider, request, cancellationToken, handler);
            var response = await pipeline().ConfigureAwait(false);

            return response;
        }
        catch (Exception exception)
        {
            var handlingResult = await RequestExceptionProcessor<TRequest, TResponse>.TryHandle(
                serviceProvider,
                request,
                exception,
                cancellationToken).ConfigureAwait(false);

            if (handlingResult.Handled)
            {
                return handlingResult.Response;
            }

            await RequestExceptionProcessor<TRequest, TResponse>.ExecuteActions(
                serviceProvider,
                request,
                exception,
                cancellationToken).ConfigureAwait(false);
            RequestExceptionProcessor<TRequest, TResponse>.Rethrow(exception);

            throw;
        }
    }

    /// <summary>
    /// Executes registered request preprocessors.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve preprocessors.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when all preprocessors have run.</returns>
    private static async Task ExecutePreProcessors(IServiceProvider serviceProvider, TRequest request, CancellationToken cancellationToken)
    {
        var preprocessors = ServiceProviderUtilities.GetServices<IRequestPreProcessor<TRequest>>(serviceProvider);

        foreach (var preprocessor in preprocessors)
        {
            await preprocessor.Process(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a request processing delegate including behaviors and postprocessors.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="handler">The terminal handler delegate.</param>
    /// <returns>The composed request processing delegate.</returns>
    private static RequestHandlerDelegate<TResponse> BuildPipeline(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken,
        RequestHandlerDelegate<TResponse> handler)
    {
        var postProcessors = ServiceProviderUtilities.GetServices<IRequestPostProcessor<TRequest, TResponse>>(serviceProvider);
        var behaviors = ServiceProviderUtilities.GetServices<IPipelineBehavior<TRequest, TResponse>>(serviceProvider);

        RequestHandlerDelegate<TResponse> current = async () =>
        {
            var response = await handler().ConfigureAwait(false);

            foreach (var postProcessor in postProcessors)
            {
                await postProcessor.Process(request, response, cancellationToken).ConfigureAwait(false);
            }

            return response;
        };

        if (behaviors is IList<IPipelineBehavior<TRequest, TResponse>> behaviorList)
        {
            for (var index = behaviorList.Count - 1; index >= 0; index--)
            {
                var behavior = behaviorList[index];
                var next = current;
                current = () => behavior.Handle(request, next, cancellationToken);
            }

            return current;
        }

        var behaviorArray = behaviors.ToArray();

        for (var index = behaviorArray.Length - 1; index >= 0; index--)
        {
            var behavior = behaviorArray[index];
            var next = current;
            current = () => behavior.Handle(request, next, cancellationToken);
        }

        return current;
    }
}