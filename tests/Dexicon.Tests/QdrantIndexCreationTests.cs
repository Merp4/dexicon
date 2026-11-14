using Dexicon.Core.Vectors;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Dexicon.Tests;

/// <summary>
/// The payload indexes a collection gets when it is first used. Qdrant answers creating an index that is
/// already there with success, so a failure is a real one, and a collection that missed an index has to be
/// tried again without a restart.
/// </summary>
public sealed class QdrantIndexCreationTests
{
    private const string Collection = "dexicon__ollama__embeddinggemma__768";
    private const int IndexedFields = 8;

    /// <summary>A channel that answers every call and can fail the creation of a payload index.</summary>
    private sealed class FlakyIndexInvoker : CallInvoker
    {
        public List<IMessage> Requests { get; } = [];

        /// <summary>Returns an exception to fail this index creation with, or null to let it succeed.</summary>
        public Func<CreateFieldIndexCollection, Exception?> FailIndex { get; set; } = _ => null;

        public int IndexRequests => Requests.OfType<CreateFieldIndexCollection>().Count();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Requests.Add((IMessage)request);

            if (request is CreateFieldIndexCollection index && FailIndex(index) is { } failure)
            {
                return new AsyncUnaryCall<TResponse>(
                    Task.FromException<TResponse>(failure), Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.Unavailable, "down"), () => new Metadata(), () => { });
            }

            var response = typeof(TResponse) == typeof(CollectionExistsResponse)
                ? (TResponse)(object)new CollectionExistsResponse { Result = new CollectionExists { Exists = true } }
                : Activator.CreateInstance<TResponse>();

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }

    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingLog : ILogger<QdrantVectorStore>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (QdrantVectorStore Store, FlakyIndexInvoker Invoker, RecordingLog Log, MovableClock Clock) Store()
    {
        var invoker = new FlakyIndexInvoker();
        var log = new RecordingLog();
        var clock = new MovableClock();
        return (new QdrantVectorStore(new QdrantClient(new QdrantGrpcClient(invoker)), log, clock), invoker, log, clock);
    }

    private static RpcException Unavailable() => new(new Status(StatusCode.Unavailable, "down"));

    [Fact]
    public async Task AFailedIndexCreationIsAWarningThatNamesTheFieldAndDoesNotStopTheCollectionBeingUsed()
    {
        var (store, invoker, log, _) = Store();
        invoker.FailIndex = index => index.FieldName == "section" ? Unavailable() : null;

        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);

        invoker.IndexRequests.ShouldBe(IndexedFields, "the other indexes are still created");
        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(1);
        warnings[0].Message.ShouldContain("section");
        warnings[0].Message.ShouldContain(Collection);
    }

    [Fact]
    public async Task ACollectionThatMissedAnIndexIsNotTriedAgainBeforeTheDelay()
    {
        var (store, invoker, log, clock) = Store();
        invoker.FailIndex = index => index.FieldName == "section" ? Unavailable() : null;
        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);

        clock.Advance(QdrantVectorStore.IndexRetryDelay - TimeSpan.FromSeconds(1));
        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);

        invoker.IndexRequests.ShouldBe(IndexedFields, "a persistent failure costs one attempt a minute, not one per call");
        log.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1);
    }

    [Fact]
    public async Task ACollectionThatMissedAnIndexIsTriedAgainAfterTheDelayAndThenLeftAlone()
    {
        var (store, invoker, _, clock) = Store();
        var failures = 1;
        invoker.FailIndex = index => index.FieldName == "section" && failures-- > 0 ? Unavailable() : null;
        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);
        invoker.IndexRequests.ShouldBe(IndexedFields);

        clock.Advance(QdrantVectorStore.IndexRetryDelay);
        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);
        invoker.IndexRequests.ShouldBe(IndexedFields * 2, "the second attempt covers every field, as the first did");

        clock.Advance(TimeSpan.FromDays(1));
        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);
        invoker.IndexRequests.ShouldBe(IndexedFields * 2, "once every index is created the collection is remembered as set up");
    }

    [Fact]
    public async Task ACollectionWhoseIndexesAllSucceedIsSetUpOnceAndLogsNothing()
    {
        var (store, invoker, log, _) = Store();

        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);
        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);

        invoker.IndexRequests.ShouldBe(IndexedFields);
        log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ACancelledIndexCreationIsNotSwallowed()
    {
        var (store, invoker, _, _) = Store();
        using var cancel = new CancellationTokenSource();
        invoker.FailIndex = _ =>
        {
            cancel.Cancel();
            return new OperationCanceledException(cancel.Token);
        };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.EnsureCollectionAsync(Collection, 768, cancel.Token));

        invoker.IndexRequests.ShouldBe(1, "creation stops at the cancelled call and does not go on to the other fields");
    }
}
