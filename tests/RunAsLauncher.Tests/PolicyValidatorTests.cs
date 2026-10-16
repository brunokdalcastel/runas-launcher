using RunAsLauncher.Policies;

namespace RunAsLauncher.Tests;

public sealed class PolicyValidatorTests
{
    private const string ValidHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    private static PolicyItem ValidItem(string id = "notepad") => new(
        Id: id,
        Title: "Notepad",
        TargetPath: @"C:\Windows\System32\notepad.exe",
        Arguments: null,
        WorkingDirectory: null,
        Mode: RunMode.Standard,
        Window: WindowMode.Normal,
        Children: ChildProcessPolicy.Allow,
        Sha256: ValidHash,
        Publisher: null,
        ExpectedSize: null,
        RequireProtectedLocation: true,
        AllowedSids: new[] { "S-1-5-21-111-222-333-1001" },
        IconPath: null);

    private static Policy PolicyOf(params PolicyItem[] items) => new(1, items);

    private static bool HasCode(ValidationResult r, string code) => r.Messages.Any(m => m.Code == code);

    [Fact]
    public void ValidPolicy_IsValid()
    {
        var result = PolicyValidator.Validate(PolicyOf(ValidItem()));
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
        var result = PolicyValidator.Validate(PolicyOf(ValidItem("dup"), ValidItem("dup")));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "DUPLICATE_ID"));
    }

    [Fact]
    public void NoHashAndNoPublisher_IsRejected()
    {
        var item = ValidItem() with { Sha256 = null, Publisher = null };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "NO_HASH_OR_PUBLISHER"));
    }

    [Fact]
    public void PublisherOnly_IsValid()
    {
        var item = ValidItem() with { Sha256 = null, Publisher = new PublisherRule("CN=Microsoft Corporation", null) };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void MalformedHash_IsRejected()
    {
        var item = ValidItem() with { Sha256 = "xyz" };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "INVALID_HASH"));
    }

    [Fact]
    public void LowercaseHash_IsRejected()
    {
        var item = ValidItem() with { Sha256 = ValidHash.ToLowerInvariant() };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "INVALID_HASH"));
    }

    [Fact]
    public void RelativeTargetPath_IsRejected()
    {
        var item = ValidItem() with { TargetPath = @"notepad.exe" };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "TARGET_NOT_ABSOLUTE"));
    }

    [Fact]
    public void UncTargetPath_IsRejected()
    {
        var item = ValidItem() with { TargetPath = @"\\server\share\app.exe" };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "TARGET_UNC"));
    }

    [Fact]
    public void NonExeTarget_IsRejected()
    {
        var item = ValidItem() with { TargetPath = @"C:\Windows\System32\cmd.bat" };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "TARGET_NOT_EXE"));
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("toolongtoolongtoolongtoolongtoolongtoolongx")] // 41 chars
    public void InvalidSlugId_IsRejected(string id)
    {
        var item = ValidItem() with { Id = id };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "INVALID_ID"));
    }

    [Fact]
    public void UnknownSchema_IsRejected()
    {
        var result = PolicyValidator.Validate(new Policy(999, new[] { ValidItem() }));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "SCHEMA_VERSION"));
    }

    [Fact]
    public void InvalidSid_IsRejected()
    {
        var item = ValidItem() with { AllowedSids = new[] { "not-a-sid" } };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "INVALID_SID"));
    }

    [Fact]
    public void EveryoneSid_IsWarningButValid()
    {
        var item = ValidItem() with { AllowedSids = new[] { "S-1-1-0" } };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Code == "EVERYONE_SID");
    }

    [Fact]
    public void TooManyItems_IsRejected()
    {
        var items = Enumerable.Range(0, PolicyValidator.MaxItems + 1)
            .Select(i => ValidItem($"item-{i}"))
            .ToArray();
        var result = PolicyValidator.Validate(PolicyOf(items));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "TOO_MANY_ITEMS"));
    }

    [Fact]
    public void RelativeWorkingDirectory_IsRejected()
    {
        var item = ValidItem() with { WorkingDirectory = @"relative\path" };
        var result = PolicyValidator.Validate(PolicyOf(item));
        Assert.False(result.IsValid);
        Assert.True(HasCode(result, "WORKDIR_NOT_ABSOLUTE"));
    }
}
