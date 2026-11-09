using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies stream request behavior for the mediator runtime.
/// </summary>
public sealed class MediatorStreamTests
{
    /// <summary>
    /// Verifies that stream requests return all values from the registered handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenHandlerRegistered_ReturnsAllValues()
    {
        var handler = new CountingStreamHandler();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<CountStreamRequest, int>), handler));
        var mediator = new MediatorRuntime(provider);

        var stream = mediator.CreateStream(new CountStreamRequest(4), CancellationToken.None);
        var values = await ToListAsync(stream, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], values);
    }

    /// <summary>
    /// Verifies that stream requests without a registered handler throw.
    /// </summary>
    [Fact]
    public async Task CreateStream_WhenNoHandlerRegistered_ThrowsInvalidOperationException()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());

        var stream = mediator.CreateStream(new CountStreamRequest(2), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToListAsync(stream, CancellationToken.None));

        Assert.Contains("No stream request handler is registered", exception.Message);
    }

    /// <summary>
    /// Verifies that the cancellation token passed to <c>CreateStream</c> is forwarded to the handler.
    /// </summary>
    [Fact]
    public async Task CreateStream_PassesCancellationTokenToHandler()
    {
        var handler = new CountingStreamHandler();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<CountStreamRequest, int>), handler));
        var mediator = new MediatorRuntime(provider);
        using var cancellation = new CancellationTokenSource();

        var stream = mediator.CreateStream(new CountStreamRequest(1), cancellation.Token);
        _ = await ToListAsync(stream, CancellationToken.None);

        Assert.Equal(cancellation.Token, handler.ReceivedToken);
    }

    /// <summary>
    /// Verifies that the enumeration cancellation token is forwarded to the handler when the request token is not cancelable.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_PassesEnumerationCancellationTokenToHandler_WhenRequestTokenIsNotCancelable()
    {
        var handler = new CountingStreamHandler();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<CountStreamRequest, int>), handler));
        var mediator = new MediatorRuntime(provider);
        using var enumerationCancellation = new CancellationTokenSource();
        var stream = mediator.CreateStream(new CountStreamRequest(10), CancellationToken.None);

        await using var enumerator = stream.WithCancellation(enumerationCancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        enumerationCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = await enumerator.MoveNextAsync();
        });
        Assert.True(handler.ReceivedToken.CanBeCanceled);
        Assert.True(handler.ReceivedToken.IsCancellationRequested);
    }

    /// <summary>
    /// Verifies that stopping after the first item disposes the handler's enumerator, so its <c>finally</c> block runs.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenConsumerBreaksAfterFirstItem_RunsHandlerFinally()
    {
        var handler = new CleanupTrackingStreamHandler();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<CleanupStreamRequest, int>), handler));
        var mediator = new MediatorRuntime(provider);

        await foreach (var value in mediator.CreateStream(new CleanupStreamRequest(), CancellationToken.None))
        {
            Assert.Equal(1, value);
            Assert.False(handler.FinallyRan);

            break;
        }

        Assert.True(handler.FinallyRan);
    }

    /// <summary>
    /// Verifies that canceling either the <c>CreateStream</c> token or the <c>WithCancellation</c> token stops a stream that was given both.
    /// </summary>
    /// <param name="cancelRequestToken">Whether to cancel the <c>CreateStream</c> token instead of the <c>WithCancellation</c> token.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateStream_WhenBothTokensAreCancelable_EitherTokenStopsTheStream(bool cancelRequestToken)
    {
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<CountStreamRequest, int>), new CountingStreamHandler()));
        var mediator = new MediatorRuntime(provider);
        using var requestCancellation = new CancellationTokenSource();
        using var enumerationCancellation = new CancellationTokenSource();
        var stream = mediator.CreateStream(new CountStreamRequest(10), requestCancellation.Token);

        await using var enumerator = stream.WithCancellation(enumerationCancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        await (cancelRequestToken ? requestCancellation : enumerationCancellation).CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = await enumerator.MoveNextAsync();
        });
    }

    /// <summary>
    /// Verifies that passing a null stream request throws.
    /// </summary>
    [Fact]
    public void CreateStream_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());

        Assert.Throws<ArgumentNullException>(
            () => mediator.CreateStream<int>(null!, CancellationToken.None));
    }

    /// <summary>
    /// Enumerates an asynchronous sequence and returns its values as a list.
    /// </summary>
    /// <typeparam name="T">The sequence element type.</typeparam>
    /// <param name="source">The sequence to enumerate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the sequence values.</returns>
    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<T> results = [];

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            results.Add(item);
        }

        return results;
    }

    /// <summary>
    /// Represents a sample stream request for stream tests.
    /// </summary>
    /// <param name="Count">The number of values to stream.</param>
    private sealed record CountStreamRequest(int Count) : IStreamRequest<int>;

    /// <summary>
    /// Represents a stream request whose handler tracks cleanup.
    /// </summary>
    private sealed record CleanupStreamRequest : IStreamRequest<int>;

    /// <summary>
    /// Streams one value at a time and records when its <c>finally</c> block runs.
    /// </summary>
    private sealed class CleanupTrackingStreamHandler : IStreamRequestHandler<CleanupStreamRequest, int>
    {
        /// <summary>
        /// Gets a value indicating whether the handler's <c>finally</c> block ran.
        /// </summary>
        public bool FinallyRan { get; private set; }

        /// <inheritdoc />
        public async IAsyncEnumerable<int> Handle(
            CleanupStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                for (var value = 1; value <= 3; value++)
                {
                    await Task.Yield();

                    yield return value;
                }
            }
            finally
            {
                FinallyRan = true;
            }
        }
    }

    /// <summary>
    /// Handles <see cref="CountStreamRequest"/> requests by streaming incrementing integers.
    /// </summary>
    private sealed class CountingStreamHandler : IStreamRequestHandler<CountStreamRequest, int>
    {
        /// <summary>
        /// Gets the cancellation token received from the last request dispatch.
        /// </summary>
        public CancellationToken ReceivedToken { get; private set; }

        /// <summary>
        /// Handles a stream request and returns incrementing integers.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of stream values.</returns>
        public IAsyncEnumerable<int> Handle(CountStreamRequest request, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;

            return CountAsync(request.Count, cancellationToken);
        }

        /// <summary>
        /// Generates stream values for the given request.
        /// </summary>
        /// <param name="count">The number of values to generate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of generated values.</returns>
        private static async IAsyncEnumerable<int> CountAsync(
            int count,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return index;
                await Task.Yield();
            }
        }
    }
}