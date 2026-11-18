using System.Text;
using Naql.Files;

namespace Naql.Tests.Files;

/// <summary>
/// Making names from sources safe as file names, the same way on every OS.
/// </summary>
public sealed class FileNamesTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("dir/sub/report.pdf", "report.pdf")]
    [InlineData(@"C:\dir\report.pdf", "report.pdf")]
    [InlineData("\"quoted.txt\"", "quoted.txt")]
    [InlineData("a:b?c*d<e>f|g.txt", "a_b_c_d_e_f_g.txt")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("name. . ", "name")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData(".hidden", ".hidden")]
    public void Makes_names_safe(string name, string expected)
    {
        Assert.Equal(expected, FileNames.ToSafeName(name));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1.tar.gz", "_Com1.tar.gz")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("aux .log", "_aux .log")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("COM10", "COM10")]
    public void Avoids_names_windows_reserves(string name, string expected)
    {
        Assert.Equal(expected, FileNames.ToSafeName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("dir/")]
    [InlineData(" . . ")]
    public void Returns_null_when_nothing_usable_is_left(string? name)
    {
        Assert.Null(FileNames.ToSafeName(name));
    }

    [Fact]
    public void Shortens_long_names_and_keeps_the_extension()
    {
        var name = FileNames.ToSafeName(new string('a', 400) + ".tar.gz")!;

        Assert.Equal(FileNames.MaxBytes, Encoding.UTF8.GetByteCount(name));
        Assert.EndsWith("a.gz", name);
    }

    [Fact]
    public void Shortens_at_whole_characters()
    {
        // Each of these takes 4 bytes in UTF-8 and two chars in UTF-16
        var name = FileNames.ToSafeName(string.Concat(Enumerable.Repeat("\U0001F600", 100)) + ".txt")!;

        Assert.True(Encoding.UTF8.GetByteCount(name) <= FileNames.MaxBytes);
        Assert.EndsWith("\U0001F600.txt", name);
        Assert.DoesNotContain('\uFFFD', Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name)));
    }

    [Fact]
    public void A_long_name_still_fits_with_the_partial_extension_and_a_number()
    {
        var name = FileNames.ToSafeName(new string('x', 300))!;

        Assert.True(Encoding.UTF8.GetByteCount($"{name} (1234){FileTarget.PartialExtension}") <= 255);
    }
}
