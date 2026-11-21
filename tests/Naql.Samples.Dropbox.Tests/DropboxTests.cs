using Naql.Files;

namespace Naql.Samples.Dropbox.Tests;

/// <summary>
/// The sample Dropbox package, built only on Naql's public API: whole folders copy between disk and Dropbox through
/// resolvers alone, downloads survive expired links and continue from checkpoints, uploads continue their session,
/// and tokens refresh.
/// </summary>
public sealed class DropboxTests : IDisposable
{
    private static readonly TransferOptions Options = new()
    {
        MaxConnections = 4,
        MinSplitPartSize = 1_000,
        BufferSize = 1_000,
        Retry = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero, Jitter = false }
    };

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "NaqlDropboxTests", Guid.NewGuid().ToString("N"));
    private readonly FakeDropbox _dropbox = new();
    private readonly Tokens _tokens = new();

    public DropboxTests()
    {
        Directory.CreateDirectory(_temp);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be open on a failing test, don't hide the real failure
        }
        _dropbox.Dispose();
    }

    /// <summary>
    /// Hands out tokens like the account's sign-in: the current one, or a new one on refresh.
    /// </summary>
    private sealed class Tokens
    {
        private int _issued = 1;

        public int Refreshes { get; private set; }

        public Task<string> GetAsync(bool refresh, CancellationToken cancellationToken)
        {
            if (refresh)
            {
                Refreshes++;
                _issued++;
            }
            return Task.FromResult($"token-{_issued}");
        }
    }

    private DropboxClient Client() => DropboxClient.Create(_tokens.GetAsync, _dropbox, FakeDropbox.ApiBase, FakeDropbox.ContentBase);

    private static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private string Local(string relative) => Path.Combine(_temp, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Copies everything <paramref name="from"/> resolves to into <paramref name="to"/>, the way an app would.
    /// </summary>
    private static async Task<List<TransferResult>> CopyAsync(Uri from, Uri to, ISourceResolver[] sources, ITargetResolver[] targets, TransferOptions? options = null)
    {
        var results = new List<TransferResult>();
        await foreach (var item in Resolve.SourcesAsync(from, sources, cancellationToken: Token))
        {
            var target = await Resolve.TargetAsync(to, item.Path, item.Info, targets, cancellationToken: Token);
            results.Add(await Transfer.From(item.Source).To(target).WithOptions(options ?? Options).RunAsync(cancellationToken: Token));
        }
        return results;
    }

    [Fact]
    public async Task A_folder_on_disk_copies_into_dropbox_through_resolvers_alone()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["a.txt"] = Content(10, 1),
            ["Sub Folder/b.bin"] = Content(4_500, 2),
            ["Sub Folder/deeper/Café #1.txt"] = Content(0, 3)
        };
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Local("in/" + path))!);
            File.WriteAllBytes(Local("in/" + path), content);
        }
        var client = Client();

        var results = await CopyAsync(new Uri(Local("in")), new Uri("dropbox:/Backups"),
            [new FileSourceResolver()], [new DropboxTargetResolver(client, chunkSize: 1_000)]);

        Assert.Equal(["/Backups/Sub Folder/b.bin", "/Backups/Sub Folder/deeper/Café #1.txt", "/Backups/a.txt"], _dropbox.Paths);
        foreach (var (path, content) in files)
        {
            Assert.Equal(content, _dropbox.Read("/Backups/" + path));
        }
        // The result says where each file ended up, and what Dropbox made of it
        Assert.Contains(results, result => result.Location == "dropbox:/Backups/Sub%20Folder/b.bin" && result.TargetMetadata["id"].StartsWith("id:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dropbox_folder_copies_to_disk_in_parallel_parts_with_one_link_per_file()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["/Photos/one.jpg"] = Content(20_000, 1),
            ["/Photos/two.jpg"] = Content(5, 2),
            ["/Photos/2026/Trip/three.jpg"] = Content(12_345, 3),
            ["/Photos/2026/four.jpg"] = Content(0, 4),
            ["/Elsewhere/not this.jpg"] = Content(10, 5)
        };
        foreach (var (path, content) in files)
        {
            _dropbox.Add(path, content);
        }

        var results = await CopyAsync(new Uri("dropbox:/photos"), new Uri(Local("out") + Path.DirectorySeparatorChar),
            [new DropboxSourceResolver(Client())], [new FileTargetResolver()]);

        foreach (var (path, content) in files.Where(file => file.Key.StartsWith("/Photos/", StringComparison.Ordinal)))
        {
            Assert.Equal(content, File.ReadAllBytes(Local("out/" + path["/Photos/".Length..])));
        }
        Assert.Equal(4, results.Count);
        Assert.False(File.Exists(Local("out/not this.jpg")));
        Assert.Contains(results, result => result.Parts > 1);
        // The listing came in pages, every file asked for one link when its transfer started, and none of the
        // downloads carried the account's token
        Assert.Contains("files/list_folder/continue", _dropbox.Calls);
        Assert.Equal(4, _dropbox.Calls.Count(call => call == "files/get_temporary_link"));
        Assert.Equal(0, _dropbox.LinkRequestsWithToken);
    }

    [Fact]
    public async Task A_link_that_runs_out_during_a_download_is_replaced_and_the_download_continues()
    {
        var content = Content(30_000, 7);
        _dropbox.Add("/big.iso", content);
        // Each link serves three requests, so a download in many parts needs several
        _dropbox.LinkLife = 3;
        var path = Local("big.iso");

        var results = await CopyAsync(new Uri("dropbox:/big.iso"), new Uri(path), [new DropboxSourceResolver(Client())], [new FileTargetResolver()]);

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(content.Length, Assert.Single(results).Transferred);
        Assert.True(_dropbox.Calls.Count(call => call == "files/get_temporary_link") > 1);
    }

    [Fact]
    public async Task A_download_continues_from_its_checkpoint_with_a_new_link_in_a_later_run()
    {
        var content = Content(30_000, 8);
        _dropbox.Add("/Docs/report.pdf", content);
        _dropbox.BreakNextDownloadAfter = 5_000;
        var client = Client();
        var path = Local("report.pdf");
        var resolvers = new ISourceResolver[] { new DropboxSourceResolver(client) };
        var once = Options with { MaxConnections = 1, Retry = RetryPolicy.None };

        var first = await Resolve.SourcesAsync(new Uri("dropbox:/Docs/report.pdf"), resolvers, cancellationToken: Token).SingleAsync(Token);
        var operation = Transfer.From(first.Source).To(FileTarget.At(path)).WithOptions(once).Build();
        await Assert.ThrowsAsync<TransferFailedException>(() => operation.RunAsync(cancellationToken: Token));
        var checkpoint = operation.GetCheckpoint();
        Assert.NotNull(checkpoint);

        // A later run resolves the file again, which gives a new link; its identity is the file's id, so the
        // checkpoint still fits
        var later = await Resolve.SourcesAsync(new Uri("dropbox:/Docs/report.pdf"), resolvers, cancellationToken: Token).SingleAsync(Token);
        var result = await Transfer.From(later.Source).To(FileTarget.At(path)).WithOptions(Options).ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.True(result.Resumed >= 4_000, $"Continued after {result.Resumed} bytes");
        Assert.Equal(2, _dropbox.Calls.Count(call => call == "files/get_temporary_link"));
    }

    [Fact]
    public async Task An_interrupted_upload_continues_its_session_in_a_later_run()
    {
        var content = Content(5_500, 9);
        var source = Local("video.mp4");
        File.WriteAllBytes(source, content);
        // The third chunk fails, after two arrived
        _dropbox.FailAppend = number => number == 2;
        var resolver = new DropboxTargetResolver(Client(), chunkSize: 1_000);
        var destination = new Uri("dropbox:/Videos");

        var target = await resolver.ResolveAsync(destination, "video.mp4", null, ResolveContext.Default, Token);
        var operation = Transfer.From(FileSource.At(source)).To(target).WithOptions(Options with { Retry = RetryPolicy.None }).Build();
        await Assert.ThrowsAsync<TransferFailedException>(() => operation.RunAsync(cancellationToken: Token));
        var checkpoint = operation.GetCheckpoint();
        Assert.NotNull(checkpoint);

        var again = await resolver.ResolveAsync(destination, "video.mp4", null, ResolveContext.Default, Token);
        var result = await Transfer.From(FileSource.At(source)).To(again).WithOptions(Options).ResumeFrom(checkpoint).RunAsync(cancellationToken: Token);

        Assert.Equal(content, _dropbox.Read("/Videos/video.mp4"));
        Assert.Equal(2_000, result.Resumed);
        // One session for both runs
        Assert.Equal(1, _dropbox.Calls.Count(call => call == "files/upload_session/start"));
    }

    [Fact]
    public async Task A_chunk_whose_answer_was_lost_isnt_stored_twice()
    {
        var content = Content(3_500, 10);
        var source = Local("a.bin");
        File.WriteAllBytes(source, content);
        _dropbox.LoseAppendAnswer = number => number == 1;
        var target = new DropboxTarget(Client(), "/a.bin", chunkSize: 1_000);

        var result = await Transfer.From(FileSource.At(source)).To(target).WithOptions(Options).RunAsync(cancellationToken: Token);

        // The retry sent the chunk again, Dropbox said it had it already, and the session went on after it
        Assert.Equal(content, _dropbox.Read("/a.bin"));
        Assert.Equal(1, result.Retries);
        Assert.Equal(1, _dropbox.Calls.Count(call => call == "files/upload_session/start"));
    }

    [Fact]
    public async Task An_expired_token_is_refreshed_for_listing_downloading_and_uploading()
    {
        _dropbox.Add("/In/a.bin", Content(8_000, 11));
        _dropbox.Add("/In/b.bin", Content(3_000, 12));
        var client = Client();
        // The first token expired before the app used it
        _dropbox.ValidToken = "token-2";

        await CopyAsync(new Uri("dropbox:/In"), new Uri("dropbox:/Out"), [new DropboxSourceResolver(client)], [new DropboxTargetResolver(client, chunkSize: 1_000)]);

        Assert.Equal(_dropbox.Read("/In/a.bin"), _dropbox.Read("/Out/a.bin"));
        Assert.Equal(_dropbox.Read("/In/b.bin"), _dropbox.Read("/Out/b.bin"));
        Assert.Equal(1, _tokens.Refreshes);
    }

    [Fact]
    public async Task Dropbox_errors_become_errors_the_engine_understands()
    {
        var client = Client();
        _dropbox.Add("/taken.txt", [1]);
        _dropbox.Space = 100;

        var missing = await Assert.ThrowsAnyAsync<TransferException>(async () =>
            await Resolve.SourcesAsync(new Uri("dropbox:/nothing/here.txt"), [new DropboxSourceResolver(client)], cancellationToken: Token).ToListAsync(Token));
        var taken = await Assert.ThrowsAsync<TransferFailedException>(() =>
            Transfer.From(Streams.StreamSource.From(new MemoryStream([2]))).To(new DropboxTarget(client, "/taken.txt", overwrite: false)).RunAsync(cancellationToken: Token));
        var full = await Assert.ThrowsAsync<TransferFailedException>(() =>
            Transfer.From(Streams.StreamSource.From(new MemoryStream(new byte[500]))).To(new DropboxTarget(client, "/big.bin")).RunAsync(cancellationToken: Token));

        Assert.Equal(TransferErrorKind.NotFound, missing.Kind);
        Assert.Equal(TransferErrorKind.AlreadyExists, taken.Kind);
        Assert.Equal(TransferErrorKind.InsufficientStorage, full.Kind);
        Assert.False(full.IsTransient);
    }

    [Theory]
    [InlineData("dropbox:/", "")]
    [InlineData("dropbox:/Photos", "/Photos")]
    [InlineData("dropbox:/My%20Photos/a%20b%2Bc.jpg", "/My Photos/a b+c.jpg")]
    [InlineData("dropbox:/CamelCase/Folder/", "/CamelCase/Folder")]
    public void References_turn_into_dropbox_paths_with_their_case(string reference, string path)
    {
        Assert.Equal(path, DropboxPath.FromReference(new Uri(reference)));
    }

    [Fact]
    public async Task An_upload_stays_inside_its_destination_folder()
    {
        var resolver = new DropboxTargetResolver(Client());

        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.ResolveAsync(new Uri("dropbox:/Backups"), "../escape.txt", null, ResolveContext.Default, Token));
        await Assert.ThrowsAsync<TransferException>(async () => await resolver.ResolveAsync(new Uri("dropbox:/"), "", null, ResolveContext.Default, Token));
    }
}
