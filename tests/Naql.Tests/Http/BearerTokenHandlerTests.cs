using Naql.Http;
using Naql.Tests.Fakes;
using System.Net;

namespace Naql.Tests.Http;

/// <summary>
/// OAuth bearer tokens: added to every request, and refreshed once when the server rejects them.
/// </summary>
public sealed class BearerTokenHandlerTests
{
    private static readonly TransferOptions Options = new()
    {
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false }
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Hands out tokens like an identity service: the current one, or a new one on refresh.
    /// </summary>
    private sealed class Tokens(int expiredUntil = 1)
    {
        private int _issued = 1;
        public int Refreshes;

        public string Current => $"token-{Volatile.Read(ref _issued)}";

        // Tokens below this number are expired
        public int ValidFrom { get; set; } = expiredUntil + 1;

        public async Task<string> GetAsync(bool refresh, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (refresh)
            {
                Interlocked.Increment(ref Refreshes);
                Interlocked.Increment(ref _issued);
            }
            return Current;
        }

        public bool Accepts(HttpRequestMessage request) =>
            request.Headers.Authorization is { Scheme: "Bearer", Parameter: { } token } && int.Parse(token.Substring("token-".Length)) >= ValidFrom;
    }

    /// <summary>
    /// Answers 401 unless the request carries a valid token, and otherwise passes it to <paramref name="inner"/>.
    /// </summary>
    private sealed class Guard(Tokens tokens, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            tokens.Accepts(request) ? base.SendAsync(request, cancellationToken) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request });
    }

    private static HttpTransport Transport(Tokens tokens, HttpMessageHandler server) =>
        new(new HttpClient(new BearerTokenHandler(tokens.GetAsync, new Guard(tokens, server))) { Timeout = Timeout.InfiniteTimeSpan });

    [Fact]
    public async Task Every_request_carries_the_current_token()
    {
        var tokens = new Tokens(expiredUntil: 0);
        var server = new FakeHttpServer();
        var target = new MemoryTarget();

        await new TransferClient(Options).From(Transport(tokens, server).Get(FakeHttpServer.Uri)).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(0, tokens.Refreshes);
    }

    [Fact]
    public async Task A_download_with_an_expired_token_gets_a_fresh_one_and_is_sent_again()
    {
        var tokens = new Tokens();
        var server = new FakeHttpServer();
        var target = new MemoryTarget();

        var result = await new TransferClient(Options).From(Transport(tokens, server).Get(FakeHttpServer.Uri)).To(target).RunAsync(cancellationToken: Token);

        Assert.Equal(server.Content, target.Session.Data);
        Assert.Equal(1, tokens.Refreshes);
        // Sent again within the handler, so the transfer never saw a failure
        Assert.Equal(0, result.Retries);
    }

    [Fact]
    public async Task An_upload_with_an_expired_token_is_retried_by_the_transfer_with_the_fresh_one()
    {
        var tokens = new Tokens();
        var server = new FakeUploadServer();
        var content = new byte[5_000];
        new Random(4).NextBytes(content);

        var result = await new TransferClient(Options).From(new MemorySource(content)).To(Transport(tokens, server).Put(FakeUploadServer.Uri)).RunAsync(cancellationToken: Token);

        Assert.Equal(content, Assert.Single(server.Uploads).Body);
        Assert.Equal(1, tokens.Refreshes);
        Assert.Equal(1, result.Retries);
    }

    [Fact]
    public async Task An_api_call_whose_body_is_in_memory_is_sent_again_at_once()
    {
        var tokens = new Tokens();
        var server = new FakeUploadServer();
        var transport = Transport(tokens, server);

        using var request = new HttpRequestMessage(HttpMethod.Post, FakeUploadServer.Uri) { Content = new StringContent("{\"path\":\"/a\"}") };
        using var response = await transport.SendAsync(request, TransferSide.Source, Token);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(1, tokens.Refreshes);
        Assert.Equal("{\"path\":\"/a\"}"u8.ToArray(), Assert.Single(server.Uploads).Body);
    }

    [Fact]
    public async Task Requests_rejected_at_the_same_time_share_one_refresh()
    {
        var tokens = new Tokens();
        var transport = Transport(tokens, new FakeHttpServer());

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FakeHttpServer.Uri);
            using var response = await transport.SendAsync(request, TransferSide.Source, Token);
            return response.StatusCode;
        }));

        Assert.All(answers, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(1, tokens.Refreshes);
    }

    [Fact]
    public async Task A_fresh_token_that_is_rejected_too_fails_as_unauthorized()
    {
        // No token is ever accepted
        var tokens = new Tokens(expiredUntil: int.MaxValue - 1);
        var source = Transport(tokens, new FakeHttpServer()).Get(FakeHttpServer.Uri);

        var ex = await Assert.ThrowsAsync<TransferFailedException>(() => new TransferClient(Options).From(source).To(new MemoryTarget()).RunAsync(cancellationToken: Token));

        Assert.Equal(TransferErrorKind.Unauthorized, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Equal(1, tokens.Refreshes);
    }
}
