using Naqel.Http;
using Naqel.Core.Tests.Fakes;

namespace Naqel.Core.Tests.Http;

/// <summary>
/// Redirects, followed by the real HTTP stack against loopback servers.
/// </summary>
public sealed class RedirectTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 5_000).Select(i => (byte)i).ToArray();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    [Fact]
    public async Task Credentials_for_one_server_dont_follow_a_redirect_to_another()
    {
        using var other = new LoopbackHttpServer(_ => new LoopbackHttpServer.Response(200, None, Content));
        using var first = new LoopbackHttpServer(_ => new LoopbackHttpServer.Response(302,
            new Dictionary<string, string> { ["Location"] = other.Uri("/file.bin").AbsoluteUri }, []));
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpTransport(client) { ConfigureRequest = HttpTransport.BasicAuthentication("user", "secret") };
        var target = new MemoryTarget();

        var result = await Transfer.From(transport.Get(first.Uri("/file.bin"))).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(Content, target.Session.Data);
        Assert.True(Assert.Single(first.Requests).Headers.ContainsKey("Authorization"));
        Assert.False(Assert.Single(other.Requests).Headers.ContainsKey("Authorization"));
        Assert.Equal("file.bin", result.Source.Name);
    }

    [Fact]
    public async Task A_redirected_download_keeps_the_requested_url_as_its_identity()
    {
        using var other = new LoopbackHttpServer(_ => new LoopbackHttpServer.Response(200, new Dictionary<string, string> { ["ETag"] = "\"v1\"" }, Content));
        using var first = new LoopbackHttpServer(_ => new LoopbackHttpServer.Response(302,
            new Dictionary<string, string> { ["Location"] = other.Uri("/moved.bin").AbsoluteUri }, []));
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var requested = first.Uri("/file.bin");

        var result = await Transfer.From(new HttpTransport(client).Get(requested)).To(new MemoryTarget()).RunAsync(cancellationToken: Token);

        // A checkpoint for the URL the caller asked for still fits when the server redirects somewhere else next time
        Assert.Equal(requested.AbsoluteUri, result.Source.Identity);
        // The name comes from the final URL
        Assert.Equal("moved.bin", result.Source.Name);
    }
}
