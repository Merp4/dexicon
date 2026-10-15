using Dexicon.Core.Vectors;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Dexicon.Tests;

/// <summary>
/// A delete by filter in a collection that does not exist deleted nothing, which is what was asked. Qdrant answers
/// NotFound for it, and that used to end as a 500 that kept a corpus, a source or a chunk set from being removed once
/// its collection was gone. Any other failure of the delete is still thrown.
/// </summary>
public sealed class MissingCollectionDeleteTests
{
    private const string Collection = "dexicon__ollama__embeddinggemma__768";

    /// <summary>A gRPC channel that answers every call with one status, and counts the calls.</summary>
    private sealed class AnsweringInvoker(StatusCode code, string detail) : CallInvoker
    {
        public int Calls { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Calls++;
            var status = new Status(code, detail);
            return new AsyncUnaryCall<TResponse>(
                Task.FromException<TResponse>(new RpcException(status)), Task.FromResult(new Metadata()),
                () => status, () => new Metadata(), () => { });
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

    private static QdrantVectorStore StoreOver(CallInvoker invoker, ILogger<QdrantVectorStore>? log = null) =>
        new(new QdrantClient(new QdrantGrpcClient(invoker)), log ?? NullLogger<QdrantVectorStore>.Instance);

    private static Task Delete(QdrantVectorStore store, string which) => which switch
    {
        "corpus" => store.DeleteCorpusAsync(Collection, "corpus-1"),
        "chunk set" => store.DeleteChunkSetAsync(Collection, "set-1"),
        _ => store.DeleteFileChunksAsync(Collection, "set-1", "source-1", "a.txt"),
    };

    [Theory]
    [InlineData("corpus", LogLevel.Information)]
    [InlineData("chunk set", LogLevel.Information)]
    [InlineData("file", LogLevel.Debug)]
    public async Task ADeleteInACollectionThatDoesNotExistIsNothingToDoAndSaysSo(string which, LogLevel level)
    {
        var invoker = new AnsweringInvoker(StatusCode.NotFound, $"Not found: Collection `{Collection}` doesn't exist!");
        var logs = new RecordingLoggerFactory();

        await Delete(StoreOver(invoker, new Logger<QdrantVectorStore>(logs)), which);

        invoker.Calls.ShouldBe(1, "the delete was sent, and Qdrant's answer is what ended it");
        logs.Levels.ShouldBe([level]);
        logs.Lines.ShouldHaveSingleItem().ShouldContain(Collection);
    }

    [Theory]
    [InlineData("corpus", StatusCode.Unavailable)]
    [InlineData("chunk set", StatusCode.Unavailable)]
    [InlineData("file", StatusCode.Unavailable)]
    [InlineData("corpus", StatusCode.Unauthenticated)]
    [InlineData("corpus", StatusCode.InvalidArgument)]
    public async Task AnyOtherFailureOfTheDeleteIsStillThrown(string which, StatusCode code)
    {
        var invoker = new AnsweringInvoker(code, "refused");

        var thrown = await Should.ThrowAsync<RpcException>(() => Delete(StoreOver(invoker), which));

        thrown.StatusCode.ShouldBe(code);
        invoker.Calls.ShouldBe(1);
    }
}
