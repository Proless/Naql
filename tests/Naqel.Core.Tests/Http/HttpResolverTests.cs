using Naqel.Files;
using Naqel.Http;
using Naqel.Core.Tests.Fakes;

namespace Naqel.Core.Tests.Http;

/// <summary>
/// Resolving <c>http:</c> and <c>https:</c> links into sources and folder URLs into uploads.
/// </summary>
public sealed class HttpResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static async Task<List<ResolvedSource>> SourcesAsync(Uri reference, ISourceResolver resolver)
    {
        var all = new List<ResolvedSource>();
        await foreach (var item in Resolve.SourcesAsync(reference, [resolver], cancellationToken: Token))
        {
            all.Add(item);
        }
        return all;
    }

    [Fact]
    public async Task A_link_resolves_to_a_source_whose_name_and_length_are_known_before_transferring()
    {
        var server = new FakeHttpServer { FileName = "report.pdf" };

        var item = Assert.Single(await SourcesAsync(FakeHttpServer.Uri, new HttpSourceResolver(new HttpTransport(server.CreateClient()))));

        Assert.IsType<HttpSource>(item.Source);
        Assert.Equal("report.pdf", item.Info!.Name);
        Assert.Equal(server.Content.Length, item.Info.Length);
    }

    [Fact]
    public async Task Without_describing_resolving_sends_no_request()
    {
        var server = new FakeHttpServer();

        var item = Assert.Single(await SourcesAsync(FakeHttpServer.Uri, new HttpSourceResolver(new HttpTransport(server.CreateClient()), describe: false)));

        Assert.Null(item.Info);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public void Only_http_and_https_are_resolved()
    {
        Assert.True(new HttpSourceResolver().CanResolve(new Uri("https://example.com/a")));
        Assert.False(new HttpSourceResolver().CanResolve(new Uri("ftp://example.com/a")));
        Assert.False(new HttpTargetResolver().CanResolve(new Uri("file:///tmp/a")));
    }

    [Fact]
    public async Task A_relative_path_is_put_inside_the_folder_url_with_each_segment_escaped()
    {
        var server = new FakeUploadServer();
        var resolver = new HttpTargetResolver(new HttpTransport(server.CreateClient()));

        var target = await resolver.ResolveAsync(new Uri("http://test.local/dav/"), "My Trip/a b#1.jpg", null, ResolveContext.Default, Token);
        await Transfer.From(new MemorySource([1, 2, 3])).To(target).RunAsync(cancellationToken: Token);

        var upload = Assert.Single(server.Uploads);
        Assert.Equal(HttpMethod.Put, upload.Method);
        Assert.Equal("http://test.local/dav/My%20Trip/a%20b%231.jpg", upload.Url!.AbsoluteUri);
    }

    [Fact]
    public async Task A_destination_without_a_relative_path_is_the_url_to_upload_to()
    {
        var server = new FakeUploadServer();
        var resolver = new HttpTargetResolver(new HttpTransport(server.CreateClient()));

        var target = await resolver.ResolveAsync(new Uri("http://test.local/upload"), "", null, ResolveContext.Default, Token);
        await Transfer.From(new MemorySource([1])).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal("http://test.local/upload", Assert.Single(server.Uploads).Url!.AbsoluteUri);
    }

    [Fact]
    public async Task A_url_that_isnt_a_folder_cant_hold_a_relative_path()
    {
        var ex = await Assert.ThrowsAsync<TransferException>(async () =>
            await new HttpTargetResolver().ResolveAsync(new Uri("http://test.local/file.bin"), "a.txt", null, ResolveContext.Default, Token));

        Assert.Equal(TransferErrorKind.Unsupported, ex.Kind);
    }

    [Fact]
    public async Task A_folder_copies_to_an_http_server_through_resolvers_alone()
    {
        foreach (var (path, length) in new[] { ("a.txt", 10), ("sub/b.txt", 2_000), ("sub/deeper/c d.txt", 0) })
        {
            var file = _temp.File("in/" + path).Replace('/', Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[length]);
        }
        var server = new FakeUploadServer();
        ITargetResolver[] targets = [new HttpTargetResolver(new HttpTransport(server.CreateClient()))];
        var destination = new Uri("http://test.local/dav/backup/");

        await foreach (var item in Resolve.SourcesAsync(new Uri(_temp.File("in")), [new FileSourceResolver()], cancellationToken: Token))
        {
            var target = await Resolve.TargetAsync(destination, item.Path, item.Info, targets, cancellationToken: Token);
            await Transfer.From(item.Source).To(target).RunAsync(cancellationToken: Token);
        }

        Assert.Equal(
            ["http://test.local/dav/backup/a.txt", "http://test.local/dav/backup/sub/b.txt", "http://test.local/dav/backup/sub/deeper/c%20d.txt"],
            server.Uploads.Select(upload => upload.Url!.AbsoluteUri).OrderBy(url => url, StringComparer.Ordinal));
    }
}
