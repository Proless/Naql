using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Naql.Samples.Dropbox.Tests;

/// <summary>
/// The parts of the Dropbox API the sample uses, in memory: metadata, paged folder listings, temporary links that
/// serve ranges and expire, and upload sessions that check offsets, with tokens, errors and faults as Dropbox has them.
/// </summary>
public sealed class FakeDropbox : HttpMessageHandler
{
    public static readonly Uri ApiBase = new("https://api.test/2/");
    public static readonly Uri ContentBase = new("https://content.test/2/");
    private const string LinkHost = "dl.test";

    private readonly object _lock = new();
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FakeLink> _links = [];
    private readonly Dictionary<string, MemoryStream> _sessions = [];
    private readonly List<string> _calls = [];
    private int _ids;
    private int _appends;

    /// <summary>The token requests must carry; anything else is answered as an expired token.</summary>
    public string ValidToken { get; set; } = "token-1";

    /// <summary>The entries in one page of a folder listing.</summary>
    public int PageSize { get; set; } = 2;

    /// <summary>The requests a temporary link serves before it expires, or <c>null</c> for no end.</summary>
    public int? LinkLife { get; set; }

    /// <summary>The bytes the account holds at most, or <c>null</c> for no limit.</summary>
    public long? Space { get; set; }

    /// <summary>Which appends, numbered from 0, fail with a 500 before storing anything.</summary>
    public Func<int, bool>? FailAppend { get; set; }

    /// <summary>Which appends, numbered from 0, store their bytes and then fail with a 500, like a lost answer.</summary>
    public Func<int, bool>? LoseAppendAnswer { get; set; }

    /// <summary>The next download of more than one byte breaks off after this many bytes.</summary>
    public long? BreakNextDownloadAfter { get; set; }

    /// <summary>The endpoints called, in order, such as <c>files/list_folder</c> or <c>link</c> for a download.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Downloads from temporary links that carried a token, which they shouldn't.</summary>
    public int LinkRequestsWithToken { get; private set; }

    public void Add(string path, byte[] content)
    {
        lock (_lock)
        {
            _files[path] = new FakeFile(path, $"id:{++_ids:x8}", $"rev{_ids:x8}", content);
        }
    }

    public byte[]? Read(string path)
    {
        lock (_lock)
        {
            return _files.TryGetValue(path, out var file) ? file.Content : null;
        }
    }

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_lock)
            {
                return [.. _files.Values.Select(file => file.Path).Order(StringComparer.Ordinal)];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == LinkHost)
        {
            return Download(request);
        }

        var endpoint = uri.AbsolutePath["/2/".Length..];
        lock (_lock)
        {
            _calls.Add(endpoint);
        }
        if (request.Headers.Authorization is not { Scheme: "Bearer" } authorization || authorization.Parameter != ValidToken)
        {
            return Reply(request, HttpStatusCode.Unauthorized, Error("expired_access_token/", "expired_access_token"));
        }

        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var arguments = uri.Host == ContentBase.Host
            ? JsonNode.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single())!
            : JsonNode.Parse(Encoding.UTF8.GetString(body))!;
        lock (_lock)
        {
            return endpoint switch
            {
                "files/get_metadata" => GetMetadata(request, (string)arguments["path"]!),
                "files/list_folder" => List(request, (string)arguments["path"]!, (bool)arguments["recursive"]!, 0),
                "files/list_folder/continue" => Continue(request, (string)arguments["cursor"]!),
                "files/get_temporary_link" => TemporaryLink(request, (string)arguments["path"]!),
                "files/upload_session/start" => Start(request, body),
                "files/upload_session/append_v2" => Append(request, arguments["cursor"]!, body),
                "files/upload_session/finish" => Finish(request, arguments["cursor"]!, arguments["commit"]!),
                _ => Reply(request, HttpStatusCode.BadRequest, $"Unknown endpoint {endpoint}")
            };
        }
    }

    private HttpResponseMessage GetMetadata(HttpRequestMessage request, string path)
    {
        if (Find(path) is { } file)
        {
            return Reply(request, HttpStatusCode.OK, FileJson(file));
        }
        return FolderPath(path) is { } folder
            ? Reply(request, HttpStatusCode.OK, FolderJson(folder))
            : Reply(request, HttpStatusCode.Conflict, Error("path/not_found/", "path"));
    }

    private HttpResponseMessage List(HttpRequestMessage request, string path, bool recursive, int offset)
    {
        if (path.Length > 0 && FolderPath(path) == null)
        {
            return Reply(request, HttpStatusCode.Conflict, Error("path/not_found/", "path"));
        }

        // Every file below the folder, and the folders on the way, as Dropbox lists them
        var prefix = path.Length == 0 ? "/" : path + "/";
        var entries = new List<object>();
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _files.Values.Where(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var rest = file.Path[prefix.Length..].Split('/');
            if (!recursive && rest.Length > 1)
            {
                folders.Add(file.Path[..(prefix.Length + rest[0].Length)]);
                continue;
            }
            for (var depth = 1; depth < rest.Length; depth++)
            {
                folders.Add(file.Path[..(prefix.Length + string.Join('/', rest.Take(depth)).Length)]);
            }
            entries.Add(FileJson(file));
        }
        entries.InsertRange(0, folders.Select(FolderJson));

        var page = entries.Skip(offset).Take(PageSize).ToList();
        var next = offset + page.Count;
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{path}|{recursive}|{next}"));
        return Reply(request, HttpStatusCode.OK, new Dictionary<string, object> { ["entries"] = page, ["cursor"] = cursor, ["has_more"] = next < entries.Count });
    }

    private HttpResponseMessage Continue(HttpRequestMessage request, string cursor)
    {
        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
        return List(request, parts[0], bool.Parse(parts[1]), int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    private HttpResponseMessage TemporaryLink(HttpRequestMessage request, string path)
    {
        if (Find(path) is not { } file)
        {
            return Reply(request, HttpStatusCode.Conflict, Error("path/not_found/", "path"));
        }
        var id = $"link{++_ids}";
        _links[id] = new FakeLink(file.Path, LinkLife);
        return Reply(request, HttpStatusCode.OK, new Dictionary<string, object> { ["metadata"] = FileJson(file), ["link"] = $"https://{LinkHost}/{id}" });
    }

    private HttpResponseMessage Download(HttpRequestMessage request)
    {
        FakeFile file;
        Stream? breaking = null;
        long start;
        long end;
        lock (_lock)
        {
            _calls.Add("link");
            if (request.Headers.Authorization != null)
            {
                LinkRequestsWithToken++;
            }
            if (!_links.TryGetValue(request.RequestUri!.AbsolutePath.TrimStart('/'), out var link) || link.Remaining == 0 || Find(link.Path) is not { } found)
            {
                return new HttpResponseMessage(HttpStatusCode.Gone) { RequestMessage = request };
            }
            link.Remaining--;
            file = found;

            var range = request.Headers.Range?.Ranges.Single();
            if (range != null && file.Content.Length == 0)
            {
                // No byte of an empty file can be asked for, as with any HTTP server
                var empty = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request, Content = new ByteArrayContent([]) };
                empty.Content.Headers.ContentRange = new ContentRangeHeaderValue(0);
                return empty;
            }
            start = range?.From ?? 0;
            end = Math.Min(range?.To ?? file.Content.Length - 1, file.Content.Length - 1);
            if (BreakNextDownloadAfter is long after && end - start + 1 > 1)
            {
                BreakNextDownloadAfter = null;
                breaking = new BreakingStream(new MemoryStream(file.Content, (int)start, (int)(end - start + 1)), after);
            }
        }

        var partial = request.Headers.Range != null;
        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = breaking != null ? new StreamContent(breaking) : new ByteArrayContent(file.Content, (int)start, (int)(end - start + 1))
        };
        response.Headers.ETag = new EntityTagHeaderValue($"\"{file.Rev}\"");
        response.Headers.AcceptRanges.Add("bytes");
        response.Content.Headers.ContentLength = end - start + 1;
        if (partial)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, file.Content.Length);
        }
        return response;
    }

    private HttpResponseMessage Start(HttpRequestMessage request, byte[] body)
    {
        var id = $"session{++_ids}";
        _sessions[id] = new MemoryStream();
        _sessions[id].Write(body);
        return Reply(request, HttpStatusCode.OK, new Dictionary<string, object> { ["session_id"] = id });
    }

    private HttpResponseMessage Append(HttpRequestMessage request, JsonNode cursor, byte[] body)
    {
        var number = _appends++;
        if (!_sessions.TryGetValue((string)cursor["session_id"]!, out var session))
        {
            return Reply(request, HttpStatusCode.Conflict, Error("not_found/", "not_found"));
        }
        if (FailAppend?.Invoke(number) == true)
        {
            return Reply(request, HttpStatusCode.InternalServerError, "Something went wrong");
        }
        if ((long)cursor["offset"]! != session.Length)
        {
            return Reply(request, HttpStatusCode.Conflict, IncorrectOffset(session.Length));
        }
        session.Write(body);
        return LoseAppendAnswer?.Invoke(number) == true
            ? Reply(request, HttpStatusCode.InternalServerError, "Something went wrong")
            : Reply(request, HttpStatusCode.OK, null);
    }

    private HttpResponseMessage Finish(HttpRequestMessage request, JsonNode cursor, JsonNode commit)
    {
        var id = (string)cursor["session_id"]!;
        if (!_sessions.TryGetValue(id, out var session))
        {
            return Reply(request, HttpStatusCode.Conflict, Error("lookup_failed/not_found/", "lookup_failed"));
        }
        if ((long)cursor["offset"]! != session.Length)
        {
            return Reply(request, HttpStatusCode.Conflict, IncorrectOffset(session.Length));
        }

        var path = (string)commit["path"]!;
        var existing = Find(path);
        if (existing != null && (string)commit["mode"]! == "add")
        {
            return Reply(request, HttpStatusCode.Conflict, Error("path/conflict/file/", "path"));
        }
        if (Space is long space && _files.Values.Where(file => file != existing).Sum(file => (long)file.Content.Length) + session.Length > space)
        {
            return Reply(request, HttpStatusCode.Conflict, Error("path/insufficient_space/", "path"));
        }

        var stored = new FakeFile(existing?.Path ?? path, existing?.Id ?? $"id:{++_ids:x8}", $"rev{++_ids:x8}", session.ToArray());
        if (existing != null)
        {
            _files.Remove(existing.Path);
        }
        _files[stored.Path] = stored;
        _sessions.Remove(id);
        return Reply(request, HttpStatusCode.OK, FileJson(stored));
    }

    private FakeFile? Find(string path) =>
        path.StartsWith("id:", StringComparison.Ordinal) ? _files.Values.FirstOrDefault(file => file.Id == path) : _files.GetValueOrDefault(path);

    // A folder exists while a file is inside it; its display path keeps the case the files have
    private string? FolderPath(string path)
    {
        var prefix = path + "/";
        return _files.Values.FirstOrDefault(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Path[..path.Length];
    }

    private static Dictionary<string, object> FileJson(FakeFile file) => new()
    {
        [".tag"] = "file",
        ["name"] = file.Path[(file.Path.LastIndexOf('/') + 1)..],
        ["path_display"] = file.Path,
        ["path_lower"] = file.Path.ToLowerInvariant(),
        ["id"] = file.Id,
        ["size"] = file.Content.Length,
        ["rev"] = file.Rev,
        ["server_modified"] = "2026-11-01T10:00:00Z"
    };

    private static Dictionary<string, object> FolderJson(string path) => new()
    {
        [".tag"] = "folder",
        ["name"] = path[(path.LastIndexOf('/') + 1)..],
        ["path_display"] = path,
        ["path_lower"] = path.ToLowerInvariant(),
        ["id"] = "id:folder" + path.ToLowerInvariant()
    };

    private static Dictionary<string, object> Error(string summary, string tag) =>
        new() { ["error_summary"] = summary, ["error"] = new Dictionary<string, object> { [".tag"] = tag } };

    private static Dictionary<string, object> IncorrectOffset(long correct) => new()
    {
        ["error_summary"] = "incorrect_offset/..",
        ["error"] = new Dictionary<string, object> { [".tag"] = "incorrect_offset", ["correct_offset"] = correct }
    };

    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, object? json) => new(status)
    {
        RequestMessage = request,
        Content = json is string text ? new StringContent(text) : new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json")
    };

    private sealed record FakeFile(string Path, string Id, string Rev, byte[] Content);

    private sealed class FakeLink(string path, int? life)
    {
        public string Path { get; } = path;
        public int? Remaining { get; set; } = life;
    }

    /// <summary>
    /// Reads normally, then fails after <paramref name="after"/> bytes, like a connection that breaks off.
    /// </summary>
    private sealed class BreakingStream(Stream inner, long after) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= after)
            {
                throw new IOException("The connection was reset");
            }
            var read = inner.Read(buffer, offset, (int)Math.Min(count, after - _read));
            _read += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
