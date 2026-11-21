using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Naqel.Core.Tests.Fakes;

/// <summary>
/// A minimal HTTP/1.1 server on a loopback port, for tests that need the real HTTP stack, such as following redirects.
/// It answers one request per connection and records each request's path and headers.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, Response> _respond;
    private readonly List<Request> _requests = [];
    private readonly Task _loop;

    public LoopbackHttpServer(Func<string, Response> respond)
    {
        _respond = respond;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public sealed record Request(string Path, IReadOnlyDictionary<string, string> Headers);

    public sealed record Response(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    public Uri Uri(string path) => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");

    public IReadOnlyList<Request> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was stopped while accepting
        }
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var head = await ReadHeadAsync(stream);
            if (head == null)
            {
                return;
            }

            var lines = head.Split(["\r\n"], StringSplitOptions.None);
            var path = lines[0].Split(' ')[1];
            var headers = lines.Skip(1).Where(line => line.Contains(':'))
                .ToDictionary(line => line.Substring(0, line.IndexOf(':')).Trim(), line => line.Substring(line.IndexOf(':') + 1).Trim(), StringComparer.OrdinalIgnoreCase);
            lock (_requests)
            {
                _requests.Add(new Request(path, headers));
            }

            var response = _respond(path);
            var text = new StringBuilder($"HTTP/1.1 {response.Status} {(HttpStatusCode)response.Status}\r\n");
            foreach (var header in response.Headers)
            {
                text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            text.Append("Content-Length: ").Append(response.Body.Length).Append("\r\nConnection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(text.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.WriteAsync(response.Body, 0, response.Body.Length);
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, 0, 1) == 1)
        {
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer.ToArray(), 0, n - 4);
            }
        }
        return null;
    }
}
