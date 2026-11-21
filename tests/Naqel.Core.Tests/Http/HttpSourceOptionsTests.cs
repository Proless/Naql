using Naqel.Http;
using Naqel.Core.Tests.Fakes;
using System.Net;

namespace Naqel.Core.Tests.Http;

/// <summary>
/// Sources for services that hand out links of their own: a stable identity, a name from the service, and signed
/// links that expire and are refreshed.
/// </summary>
public sealed class HttpSourceOptionsTests
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false },
        MinSplitPartSize = 10_000
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Uri Signed = new("http://test.local/file.bin?signature=old");
    private static readonly Uri Renewed = new("http://test.local/file.bin?signature=new");

    /// <summary>
    /// A server that answers 403 to the old signature once <paramref name="expired"/> says so.
    /// </summary>
    private static FakeHttpServer Server(Func<bool> expired) => new()
    {
        Intercept = request => request.RequestUri!.Query.Contains("signature=old") && expired() ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null
    };

    private static Task<TransferResult> RunAsync(HttpSource source, MemoryTarget target, TransferOptions? options = null)
    {
        return new TransferClient(options ?? Options).From(source).To(target).RunAsync(cancellationToken: Token);
    }

    [Fact]
    public async Task The_identity_and_name_come_from_the_options()
    {
        var server = new FakeHttpServer();
        var source = new HttpTransport(server.CreateClient()).Get(Signed, new HttpSourceOptions { Identity = "gdrive:1AbC", Name = "Report.pdf" });

        var result = await RunAsync(source, new MemoryTarget());

        Assert.Equal("gdrive:1AbC", result.Source.Identity);
        Assert.Equal("Report.pdf", result.Source.Name);
    }

    [Fact]
    public async Task An_expired_link_is_refreshed_and_the_request_sent_again()
    {
        var server = Server(() => true);
        var refreshes = 0;
        var source = new HttpTransport(server.CreateClient()).Get(Signed, new HttpSourceOptions
        {
            Refresh = _ =>
            {
                Interlocked.Increment(ref refreshes);
                return Task.FromResult(Renewed);
            }
        });
        var target = new MemoryTarget();

        var result = await RunAsync(source, target);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(1, refreshes);
        Assert.Equal(0, result.Retries);
        // The identity is still the link the source was given, so checkpoints keep fitting
        Assert.Equal(Signed.AbsoluteUri, result.Source.Identity);
    }

    [Fact]
    public async Task Parts_that_find_the_link_expired_at_the_same_time_share_one_refresh()
    {
        // The link expires right after the server described the content, so every part finds it expired
        var probed = 0;
        var server = Server(() => Volatile.Read(ref probed) > 0);
        var refreshes = 0;
        var source = new HttpTransport(server.CreateClient()).Get(Signed, new HttpSourceOptions
        {
            Refresh = async _ =>
            {
                Interlocked.Increment(ref refreshes);
                await Task.Delay(50, Token);
                return Renewed;
            }
        });
        var target = new MemoryTarget { OnWrite = (_, _, _) => Task.CompletedTask };
        server.Intercept = request =>
        {
            if (request.Headers.Range?.Ranges.Single() is { From: 0, To: 0 })
            {
                Interlocked.Increment(ref probed);
                return null;
            }
            return request.RequestUri!.Query.Contains("signature=old") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null;
        };

        var result = await RunAsync(source, target, Options with { MaxConnections = 4 });

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(4, result.Parts);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task Without_a_refresh_an_expired_link_fails_as_access_denied()
    {
        var source = new HttpTransport(Server(() => true).CreateClient()).Get(Signed);

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget()));

        Assert.Equal(TransferErrorKind.AccessDenied, ex.Kind);
    }

    [Fact]
    public async Task A_refresh_that_gives_another_expired_link_fails_instead_of_refreshing_forever()
    {
        var refreshes = 0;
        var source = new HttpTransport(Server(() => true).CreateClient()).Get(Signed, new HttpSourceOptions
        {
            Refresh = _ =>
            {
                Interlocked.Increment(ref refreshes);
                return Task.FromResult(Signed);
            }
        });

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => RunAsync(source, new MemoryTarget()));

        Assert.Equal(TransferErrorKind.AccessDenied, ex.Kind);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task A_checkpoint_continues_with_a_new_link_to_the_same_identity()
    {
        var server = new FakeHttpServer();
        var target = new MemoryTarget();
        var first = await target.OpenAsync(new TargetOpenRequest(new SourceInfo { Length = server.Content.Length }), Token);
        await first.WritePartAsync(new ByteRange(0, 40_000), new MemoryStream(server.Content, 0, 40_000), Token);
        var checkpoint = new TransferCheckpoint(server.Content.Length, TransferCheckpoint.Fingerprint("gdrive:1AbC"), server.ETag, "memory-0",
            [new CheckpointPart(new ByteRange(0, server.Content.Length), 40_000)]);
        // A later session gets a different signed link to the same file
        var source = new HttpTransport(server.CreateClient()).Get(Renewed, new HttpSourceOptions { Identity = "gdrive:1AbC" });

        var result = await new TransferClient(Options).From(source).To(target).ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);

        Assert.Equal(40_000, result.Resumed);
        Assert.Equal(server.Content, target.Sessions[^1].Data);
    }
}
