using Naql.Files;

namespace Naql.Tests.Transfers;

/// <summary>
/// How <see cref="Uri"/> treats references, which every resolver relies on. The design doc's advice for resolver
/// authors rests on these, so they're checked on every runtime the tests run on.
/// </summary>
public sealed class UriPitfallTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_scheme_of_two_or_more_letters_is_a_scheme()
    {
        Assert.Equal("gd", new Uri("gd:abc").Scheme);
        Assert.False(new Uri("gd:abc").IsFile);
    }

    [Fact]
    public void A_one_letter_scheme_is_read_as_a_drive_letter_on_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            // "x:1" is a relative path on drive X, which a Uri can't hold
            Assert.Throws<UriFormatException>(() => new Uri("x:1"));
            Assert.True(new Uri("x:/1").IsFile);
        }
    }

    [Fact]
    public void A_host_is_lowercased_but_an_opaque_reference_keeps_its_case()
    {
        Assert.Equal("abcxyz", new Uri("gdrive://AbcXyz/file").Host);
        // So references that differ only there are the same reference, also to loop detection
        Assert.Equal(new Uri("gdrive://abcxyz/file").AbsoluteUri, new Uri("GDrive://AbcXyz/file").AbsoluteUri);
        Assert.Equal("AbcXyz", new Uri("gdrive:AbcXyz").AbsolutePath);
        Assert.Equal("/Folder/AbcXyz", new Uri("dropbox:/Folder/AbcXyz").AbsolutePath);
    }

    [Fact]
    public void A_scheme_is_lowercased()
    {
        Assert.Equal("gdrive", new Uri("GDrive:abc").Scheme);
    }

    [Fact]
    public void Spaces_and_other_characters_in_a_path_come_back_escaped()
    {
        var reference = new Uri("dropbox:/My Photos/a b+c.jpg");

        Assert.Equal("/My%20Photos/a%20b+c.jpg", reference.AbsolutePath);
        // Unescaping each segment gives the names back
        Assert.Equal(["My Photos", "a b+c.jpg"], reference.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString));
    }

    [Theory]
    [InlineData("a#b.txt")]
    [InlineData("100%.txt")]
    [InlineData("a%20b.txt")]
    [InlineData("a b.txt")]
    public async Task A_file_name_with_characters_uris_treat_specially_resolves_to_that_file(string name)
    {
        var path = _temp.File(name);
        File.WriteAllBytes(path, [1, 2, 3]);

        var reference = new Uri(path);
        var all = new List<ResolvedSource>();
        await foreach (var item in Resolve.SourcesAsync(reference, [new FileSourceResolver()], cancellationToken: Token))
        {
            all.Add(item);
        }

        Assert.Equal(name, Assert.Single(all).Info!.Name);
        Assert.Equal(3, (await all[0].Source.GetInfoAsync(Token)).Length);
    }
}
