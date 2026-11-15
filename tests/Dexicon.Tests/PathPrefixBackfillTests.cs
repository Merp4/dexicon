using Dexicon.Core.Search;
using Dexicon.Core.Vectors;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Dexicon.Tests;

/// <summary>
/// The payload that makes <c>pathPrefix</c> a prefix: written on every chunk when it is stored, and, for chunks
/// stored before it existed, in place by <see cref="QdrantVectorStore.BackfillPathPrefixesAsync"/>. The store is
/// driven through a fake channel that answers each call from a script and records what it was asked.
/// </summary>
public sealed class PathPrefixBackfillTests
{
    private const string Collection = "dexicon__ollama__embeddinggemma__768";

    /// <summary>A channel that answers calls from a script and keeps every request.</summary>
    private sealed class ScriptedInvoker : CallInvoker
    {
        public List<IMessage> Requests { get; } = [];

        /// <summary>The answer to a request, or null for an empty one.</summary>
        public Func<IMessage, IMessage?> Respond { get; set; } = _ => null;

        public Func<CreateFieldIndexCollection, Exception?> FailIndex { get; set; } = _ => null;

        public IEnumerable<T> Of<T>() where T : IMessage => Requests.OfType<T>();

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

            var response = Respond((IMessage)request) is TResponse scripted ? scripted : Activator.CreateInstance<TResponse>();
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

    private static (QdrantVectorStore Store, ScriptedInvoker Invoker) Store()
    {
        var invoker = new ScriptedInvoker();
        return (new QdrantVectorStore(new QdrantClient(new QdrantGrpcClient(invoker)), NullLogger<QdrantVectorStore>.Instance), invoker);
    }

    private static ListCollectionsResponse Collections(params string[] names)
    {
        var response = new ListCollectionsResponse();
        foreach (var name in names) response.Collections.Add(new CollectionDescription { Name = name });
        return response;
    }

    private static RetrievedPoint Point(string id, string filePath)
    {
        var point = new RetrievedPoint { Id = new PointId { Uuid = id } };
        point.Payload.Add("file_path", filePath);
        return point;
    }

    private const string A1 = "00000000-0000-0000-0000-000000000001";
    private const string A2 = "00000000-0000-0000-0000-000000000002";
    private const string B1 = "00000000-0000-0000-0000-000000000003";
    private const string C1 = "00000000-0000-0000-0000-000000000004";

    /// <summary>Two pages of chunks without the field, the first with a next page.</summary>
    private static Func<IMessage, IMessage?> TwoPages(Queue<ScrollResponse> pages) =>
        request => request switch
        {
            ListCollectionsRequest => Collections(Collection, "an-unrelated-collection"),
            ScrollPoints => pages.Count > 0 ? pages.Dequeue() : new ScrollResponse(),
            _ => null,
        };

    private static Queue<ScrollResponse> PagesOfThreeFiles()
    {
        var first = new ScrollResponse { NextPageOffset = new PointId { Uuid = B1 } };
        first.Result.Add(Point(A1, "src/Auth/a.cs"));
        first.Result.Add(Point(A2, "src/Auth/a.cs"));
        first.Result.Add(Point(B1, "Readme.md"));
        var second = new ScrollResponse();
        second.Result.Add(Point(C1, "lib/Deep/er/c.cs"));
        return new Queue<ScrollResponse>([first, second]);
    }

    [Fact]
    public async Task TheBackfillWritesTheFoldersOfEachFileOnceForTheChunksThatHaveNone()
    {
        var (store, invoker) = Store();
        invoker.Respond = TwoPages(PagesOfThreeFiles());

        var updated = await store.BackfillPathPrefixesAsync(TestContext.Current.CancellationToken);

        updated.ShouldBe(4);
        var batches = invoker.Of<UpdateBatchPoints>().ToList();
        batches.Count.ShouldBe(2, "one write per page");

        var first = batches[0].Operations.Select(o => o.SetPayload).ToList();
        first.Count.ShouldBe(2, "one operation per file of the page");
        var a = first.Single(o => o.PointsSelector.Points.Ids.Count == 2);
        a.PointsSelector.Points.Ids.Select(i => i.Uuid).ShouldBe([A1, A2]);
        a.Payload["path_prefixes"].ListValue.Values.Select(v => v.StringValue).ShouldBe(["src", "src/auth", "src/auth/a.cs"]);
        var readme = first.Single(o => o.PointsSelector.Points.Ids.Count == 1);
        readme.Payload["path_prefixes"].ListValue.Values.Select(v => v.StringValue).ShouldBe(["readme.md"]);

        batches[1].Operations.ShouldHaveSingleItem().SetPayload.Payload["path_prefixes"].ListValue.Values
            .Select(v => v.StringValue).ShouldBe(["lib", "lib/deep", "lib/deep/er", "lib/deep/er/c.cs"]);
    }

    [Fact]
    public async Task TheBackfillSelectsChunksWithNoPathPrefixesAndLeavesOtherCollectionsAlone()
    {
        var (store, invoker) = Store();
        invoker.Respond = TwoPages(PagesOfThreeFiles());

        await store.BackfillPathPrefixesAsync(TestContext.Current.CancellationToken);

        var scrolls = invoker.Of<ScrollPoints>().ToList();
        scrolls.ShouldAllBe(s => s.CollectionName == Collection);
        var filter = scrolls[0].Filter;
        filter.Must.Any(c => c.IsEmpty is { Key: "path_prefixes" }).ShouldBeTrue();
        filter.Must.Any(c => c.Field is { Key: "kind" } && c.Field.Match.Keyword == "chunk").ShouldBeTrue();
        scrolls[0].WithVectors.Enable.ShouldBeFalse();
        scrolls[0].WithPayload.Include.Fields.ShouldBe(["file_path"]);

        invoker.Of<CreateFieldIndexCollection>().Where(i => i.FieldName == "path_prefixes")
            .ShouldHaveSingleItem().CollectionName.ShouldBe(Collection);
        invoker.Requests.OfType<CreateFieldIndexCollection>().ShouldAllBe(i => i.CollectionName == Collection);
    }

    [Fact]
    public async Task ACollectionWithNothingToDoCostsOneScrollAndNoWrite()
    {
        var (store, invoker) = Store();
        invoker.Respond = request => request is ListCollectionsRequest ? Collections(Collection) : null;

        var updated = await store.BackfillPathPrefixesAsync(TestContext.Current.CancellationToken);

        updated.ShouldBe(0);
        invoker.Of<ScrollPoints>().Count().ShouldBe(1);
        invoker.Of<UpdateBatchPoints>().ShouldBeEmpty();
    }

    [Fact]
    public async Task AChunkWithNoFilePathIsNotWrittenAndNotCounted()
    {
        var (store, invoker) = Store();
        var page = new ScrollResponse();
        page.Result.Add(Point(A1, ""));
        invoker.Respond = TwoPages(new Queue<ScrollResponse>([page]));

        var updated = await store.BackfillPathPrefixesAsync(TestContext.Current.CancellationToken);

        updated.ShouldBe(0);
        invoker.Of<UpdateBatchPoints>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ACollectionWhoseIndexCannotBeCreatedStopsTheBackfillWithAnErrorBeforeAnyScroll()
    {
        var (store, invoker) = Store();
        invoker.Respond = TwoPages(PagesOfThreeFiles());
        invoker.FailIndex = index => index.FieldName == "path_prefixes" ? new RpcException(new Status(StatusCode.Unavailable, "down")) : null;

        await Should.ThrowAsync<InvalidOperationException>(() => store.BackfillPathPrefixesAsync(TestContext.Current.CancellationToken));

        invoker.Of<ScrollPoints>().ShouldBeEmpty();
    }

    [Fact]
    public async Task AChunkIsStoredWithTheFoldersOfItsFile()
    {
        var (store, invoker) = Store();
        var chunk = new Chunk
        {
            CorpusId = "c1",
            ChunkSetId = "s1",
            SourceId = "src1",
            FilePath = "src/Auth/a.cs",
            FileHash = "h",
            Content = "alpha",
        };

        await store.UpsertAsync(Collection, [chunk], [new float[768]], TestContext.Current.CancellationToken);

        var point = invoker.Of<UpsertPoints>().ShouldHaveSingleItem().Points.ShouldHaveSingleItem();
        point.Payload["file_path"].StringValue.ShouldBe("src/Auth/a.cs");
        point.Payload["path_prefixes"].ListValue.Values.Select(v => v.StringValue)
            .ShouldBe(["src", "src/auth", "src/auth/a.cs"]);
    }
}
