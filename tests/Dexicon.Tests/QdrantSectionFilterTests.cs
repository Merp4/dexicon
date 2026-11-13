using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Dexicon.Tests;

/// <summary>
/// The vector store's half of the section filter: a keyword condition on the <c>section</c> payload field in
/// every query a search sends, whatever its mode, and a payload index to answer it with. Over a recording gRPC
/// channel, so what is asserted is what would be sent to Qdrant.
/// </summary>
public sealed class QdrantSectionFilterTests
{
    private const string Collection = "dexicon__ollama__embeddinggemma__768";

    /// <summary>A channel that records every request and answers with an empty response of the right type.</summary>
    private sealed class RecordingInvoker : CallInvoker
    {
        public List<IMessage> Requests { get; } = [];

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Requests.Add((IMessage)request);

            // The client reads the result of an existence check, so an empty reply would be a null reference.
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

    private static (QdrantVectorStore Store, RecordingInvoker Invoker) Store()
    {
        var invoker = new RecordingInvoker();
        return (new QdrantVectorStore(new QdrantClient(new QdrantGrpcClient(invoker)), NullLogger<QdrantVectorStore>.Instance), invoker);
    }

    private static SearchQuery Query(SearchMode mode, string? section) => new()
    {
        Text = "alpha beta",
        CorpusIds = ["corpus-1"],
        ChunkSetIds = ["set-1"],
        CollectionName = Collection,
        Mode = mode,
        Section = section,
    };

    /// <summary>The conditions of a filter on one payload field, as the key and the keyword each asks for.</summary>
    private static List<(string Key, string Keyword)> KeywordConditions(Filter? filter) =>
        [.. (filter?.Must ?? [])
            .Where(c => c.Field is { Match.Keyword.Length: > 0 })
            .Select(c => (c.Field.Key, c.Field.Match.Keyword))];

    [Theory]
    [InlineData(SearchMode.Hybrid)]
    [InlineData(SearchMode.Semantic)]
    [InlineData(SearchMode.Keyword)]
    public async Task EveryQueryASearchSendsFiltersOnTheSection(SearchMode mode)
    {
        var (store, invoker) = Store();

        await store.SearchAsync(Query(mode, "diff"), new float[768], SparseEncoder.Encode("alpha beta"), TestContext.Current.CancellationToken);

        var query = invoker.Requests.OfType<QueryPoints>().Single();
        var filters = new List<Filter?> { query.Filter };
        filters.AddRange(query.Prefetch.Select(p => p.Filter));

        filters.Count.ShouldBe(mode == SearchMode.Hybrid ? 3 : 1, "a hybrid query filters the query and both of its lists");
        foreach (var filter in filters)
            KeywordConditions(filter).ShouldContain(("section", "diff"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ASearchWithNoSectionFiltersOnNone(string? section)
    {
        var (store, invoker) = Store();

        await store.SearchAsync(Query(SearchMode.Hybrid, section), new float[768], SparseEncoder.Encode("alpha beta"), TestContext.Current.CancellationToken);

        var query = invoker.Requests.OfType<QueryPoints>().Single();
        KeywordConditions(query.Filter).ShouldNotContain(c => c.Key == "section");
    }

    [Fact]
    public async Task TheSectionFieldHasAKeywordIndex()
    {
        var (store, invoker) = Store();

        await store.EnsureCollectionAsync(Collection, 768, TestContext.Current.CancellationToken);

        var indexed = invoker.Requests.OfType<CreateFieldIndexCollection>()
            .ToDictionary(r => r.FieldName, r => r.FieldType);
        indexed.ShouldContainKey("section");
        indexed["section"].ShouldBe(FieldType.Keyword);
        indexed.ShouldContainKey("symbols", "the neighbouring filter fields are indexed in the same call");
    }
}
