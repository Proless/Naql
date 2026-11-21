using Naqel.Http;
using Naqel.Core.Tests.Fakes;
using System.Net;

namespace Naqel.Core.Tests.Http;

/// <summary>
/// The request pipeline packages for particular services use for their own API calls.
/// </summary>
public sealed class HttpPipelineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_packages_own_request_gets_the_transports_settings()
    {
        var server = new FakeHttpServer();
        HttpRequestMessage? seen = null;
        server.Intercept = request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"files\":[]}") };
        };
        var transport = new HttpTransport(server.CreateClient())
        {
            UserAgent = "Example/1.0",
            ConfigureRequest = (request, _) =>
            {
                request.Headers.Add("X-Api-Key", "key");
                return Task.CompletedTask;
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/api/list");
        using var response = await transport.SendAsync(request, TransferSide.Source, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Example/1.0", seen!.Headers.UserAgent.ToString());
        Assert.Equal("key", seen.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task An_unsuccessful_answer_becomes_the_same_error_naqels_own_endpoints_raise()
    {
        var server = new FakeHttpServer { Intercept = _ => new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(7)) } } };
        var transport = new HttpTransport(server.CreateClient());
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://test.local/api/upload/start");

        using var response = await transport.SendAsync(request, TransferSide.Target, Token);
        var error = HttpTransferException.FromResponse(response, TransferSide.Target);

        Assert.Equal(TransferErrorKind.RateLimited, error.Kind);
        Assert.True(error.IsTransient);
        Assert.Equal(TransferSide.Target, error.Side);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryDelay);
        Assert.Equal(HttpMethod.Post, error.Method);
        Assert.Equal("http://test.local/api/upload/start", error.RequestUri.AbsoluteUri);
    }

    [Fact]
    public async Task A_package_can_name_the_kind_its_service_means()
    {
        // A service that answers 403 when the daily quota is used up
        var server = new FakeHttpServer { Intercept = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
        var transport = new HttpTransport(server.CreateClient());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/api/file");
        using var response = await transport.SendAsync(request, TransferSide.Source, Token);

        var error = HttpTransferException.FromResponse(response, "The daily download quota is used up", TransferErrorKind.QuotaExceeded, isTransient: false, TransferSide.Source);

        Assert.Equal(TransferErrorKind.QuotaExceeded, error.Kind);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("The daily download quota is used up", error.Message);
    }

    [Fact]
    public async Task No_answer_is_a_transient_network_error()
    {
        var transport = new HttpTransport(new HttpClient(new ThrowingHandler()));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.local/api/file");

        var error = await Assert.ThrowsAsync<HttpTransferException>(() => transport.SendAsync(request, TransferSide.Source, Token));

        Assert.Equal(TransferErrorKind.Network, error.Kind);
        Assert.True(error.IsTransient);
        Assert.Null(error.StatusCode);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known");
    }
}
