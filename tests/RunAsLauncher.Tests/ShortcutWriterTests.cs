namespace RunAsLauncher.Tests;

public sealed class ShortcutWriterTests
{
    [Fact]
    public void CommandLine_QuotesPathWithSpaces()
    {
        string cmd = ShortcutWriter.CommandLine(@"C:\Program Files\RunAs Launcher\RunAsLauncher.exe", "cmd-admin");

        Assert.Equal("\"C:\\Program Files\\RunAs Launcher\\RunAsLauncher.exe\" run cmd-admin", cmd);
    }

    [Theory]
    [InlineData("Cmd Admin")]
    [InlineData("cmd admin")]
    [InlineData("x\" & calc")]
    [InlineData("")]
    [InlineData("a-very-long-identifier-that-exceeds-forty-chars")]
    public void CommandLine_RejectsNonSlugId(string id)
    {
        Assert.Throws<ArgumentException>(() => ShortcutWriter.CommandLine(@"C:\RunAsLauncher.exe", id));
    }

    [Fact]
    public void Sanitize_ReplacesEveryInvalidCharacter()
    {
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", ShortcutWriter.SanitizeFileName("a\\b/c:d*e?f\"g<h>i|j"));
    }

    [Fact]
    public void Sanitize_ReplacesControlCharacters()
    {
        Assert.Equal("a_b", ShortcutWriter.SanitizeFileName("a\tb"));
    }

    [Fact]
    public void Sanitize_KeepsUnicodeAndSpaces()
    {
        Assert.Equal("Edição de Registro", ShortcutWriter.SanitizeFileName("Edição de Registro"));
    }

    [Theory]
    [InlineData("  ..Prompt..  ", "Prompt")]
    [InlineData("...", "RunAs Launcher")]
    [InlineData("", "RunAs Launcher")]
    public void Sanitize_TrimsDotsAndSpaces(string title, string expected)
    {
        Assert.Equal(expected, ShortcutWriter.SanitizeFileName(title));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Com1", "_Com1")]
    [InlineData("Console", "Console")]
    public void Sanitize_AvoidsReservedDeviceNames(string title, string expected)
    {
        Assert.Equal(expected, ShortcutWriter.SanitizeFileName(title));
    }

    [Fact]
    public void Sanitize_LimitsLength()
    {
        Assert.Equal(100, ShortcutWriter.SanitizeFileName(new string('a', 300)).Length);
    }
}
