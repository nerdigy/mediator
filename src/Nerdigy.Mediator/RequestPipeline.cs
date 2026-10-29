using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Executes the request pipeline for a concrete request and response pair.
/// </summary>
/// <remarks>
/// Components run in this order: preprocessors, behaviors from outermost to innermost, the handler, then
/// postprocessors inside the innermost behavior. A failure anywhere goes to exception processing.
/// </remarks>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal static class RequestPipeline<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Executes the pipeline for a request.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="terminal">Resolves and invokes the handler.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public static Task<TResponse> Execute(
        IServiceProvider serviceProvider,
        TRequest request,
        RequestTerminal<TRequest, TResponse> terminal,
        CancellationToken cancellationToken)
    {
        var preProcessors = ServiceProviderUtilities.GetServices<IRequestPreProcessor<TRequest>>(serviceProvider);
        var behaviors = ServiceProviderUtilities.GetServices<IPipelineBehavior<TRequest, TResponse>>(serviceProvider);
        var postProcessors = ServiceProviderUtilities.GetServices<IRequestPostProcessor<TRequest, TResponse>>(serviceProvider);

        if (preProcessors.Length != 0)
        {
            var context = new PipelineContext(serviceProvider, request, terminal, behaviors, postProcessors);

            return ExecutePipeline(context, preProcessors, cancellationToken);
        }

        // Without preprocessors nothing has to run before the chain, so call it directly and return its task when it
        // completes synchronously. Only a failure or a pending task needs an async method.
        Task<TResponse> task;

        try
        {
            task = behaviors.Length == 0 && postProcessors.Length == 0
                ? terminal(serviceProvider, request, cancellationToken)
                : new PipelineContext(serviceProvider, request, terminal, behaviors, postProcessors).InvokeAt(0, cancellationToken);
        }
        catch (Exception exception)
        {
            return RequestExceptionProcessor<TRequest, TResponse>.Process(serviceProvider, request, exception, cancellationToken);
        }

        return task.IsCompletedSuccessfully
            ? task
            : AwaitPipeline(serviceProvider, request, task, cancellationToken);
    }

    /// <summary>
    /// Awaits a handler or behavior chain that did not complete synchronously and processes its failure.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve exception components.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="task">The handler task.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    private static async Task<TResponse> AwaitPipeline(
        IServiceProvider serviceProvider,
        TRequest request,
        Task<TResponse> task,
        CancellationToken cancellationToken)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await RequestExceptionProcessor<TRequest, TResponse>.Process(
                serviceProvider,
                request,
                exception,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs preprocessors and then the behavior chain, and processes any failure.
    /// </summary>
    /// <param name="context">The pipeline state shared by every link in the chain.</param>
    /// <param name="preProcessors">The resolved preprocessors.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    private static async Task<TResponse> ExecutePipeline(
        PipelineContext context,
        IRequestPreProcessor<TRequest>[] preProcessors,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var preProcessor in preProcessors)
            {
                await preProcessor.Process(context.Request, cancellationToken).ConfigureAwait(false);
            }

            return await context.InvokeAt(0, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await RequestExceptionProcessor<TRequest, TResponse>.Process(
                context.ServiceProvider,
                context.Request,
                exception,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds the state shared by every link in one request's behavior chain.
    /// </summary>
    private sealed class PipelineContext
    {
        private readonly IPipelineBehavior<TRequest, TResponse>[] _behaviors;
        private readonly IRequestPostProcessor<TRequest, TResponse>[] _postProcessors;
        private readonly RequestTerminal<TRequest, TResponse> _terminal;

        /// <summary>
        /// Initializes a new instance of the <see cref="PipelineContext"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider used to resolve the handler.</param>
        /// <param name="request">The request being processed.</param>
        /// <param name="terminal">Resolves and invokes the handler.</param>
        /// <param name="behaviors">The resolved behaviors, outermost first.</param>
        /// <param name="postProcessors">The resolved postprocessors.</param>
        public PipelineContext(
            IServiceProvider serviceProvider,
            TRequest request,
            RequestTerminal<TRequest, TResponse> terminal,
            IPipelineBehavior<TRequest, TResponse>[] behaviors,
            IRequestPostProcessor<TRequest, TResponse>[] postProcessors)
        {
            ServiceProvider = serviceProvider;
            Request = request;
            _terminal = terminal;
            _behaviors = behaviors;
            _postProcessors = postProcessors;
        }

        /// <summary>
        /// Gets the service provider used to resolve pipeline services.
        /// </summary>
        public IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the request being processed.
        /// </summary>
        public TRequest Request { get; }

        /// <summary>
        /// Invokes the behavior at <paramref name="index"/>, or the handler once every behavior has run.
        /// </summary>
        /// <param name="index">The index of the behavior to invoke.</param>
        /// <param name="cancellationToken">The cancellation token for this part of the pipeline.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<TResponse> InvokeAt(int index, CancellationToken cancellationToken)
        {
            if (index == _behaviors.Length)
            {
                return _postProcessors.Length == 0
                    ? _terminal(ServiceProvider, Request, cancellationToken)
                    : InvokeHandlerAndPostProcessors(cancellationToken);
            }

            var next = new PipelineLink(this, index + 1, cancellationToken);

            return _behaviors[index].Handle(Request, next.Invoke, cancellationToken);
        }

        /// <summary>
        /// Invokes the handler and then every postprocessor.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token passed to the handler and postprocessors.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        private async Task<TResponse> InvokeHandlerAndPostProcessors(CancellationToken cancellationToken)
        {
            var response = await _terminal(ServiceProvider, Request, cancellationToken).ConfigureAwait(false);

            foreach (var postProcessor in _postProcessors)
            {
                await postProcessor.Process(Request, response, cancellationToken).ConfigureAwait(false);
            }

            return response;
        }
    }

    /// <summary>
    /// The <c>next</c> delegate target passed to one behavior.
    /// </summary>
    /// <param name="context">The pipeline state.</param>
    /// <param name="index">The index of the behavior or handler that <c>next</c> invokes.</param>
    /// <param name="inheritedCancellationToken">The token the calling behavior received.</param>
    private sealed class PipelineLink(PipelineContext context, int index, CancellationToken inheritedCancellationToken)
    {
        /// <summary>
        /// Invokes the rest of the pipeline.
        /// </summary>
        /// <param name="cancellationToken">
        /// The token for the rest of the pipeline, or <see langword="default"/> to keep the calling behavior's token.
        /// </param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<TResponse> Invoke(CancellationToken cancellationToken)
        {
            return context.InvokeAt(
                index,
                cancellationToken == default ? inheritedCancellationToken : cancellationToken);
        }
    }
}