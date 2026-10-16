using RunAsLauncher.Identity;
using RunAsLauncher.Policies;

namespace RunAsLauncher.Tests;

public sealed class AuthorizationTests
{
    private static PolicyItem ItemWith(params string[] allowedSids) => new(
        Id: "x",
        Title: "X",
        TargetPath: @"C:\Windows\System32\notepad.exe",
        Arguments: null,
        WorkingDirectory: null,
        Mode: RunMode.Standard,
        Window: WindowMode.Normal,
        Children: ChildProcessPolicy.Allow,
        Sha256: null,
        Publisher: new PublisherRule("Contoso", null),
        ExpectedSize: null,
        RequireProtectedLocation: true,
        AllowedSids: allowedSids,
        IconPath: null);

    private const string User = "S-1-5-21-1-2-3-1001";
    private const string Group = "S-1-5-21-1-2-3-513";

    [Fact]
    public void UserSidInAllowedSids_IsAllowed()
    {
        Assert.True(Authorization.IsAllowed(ItemWith(User), User, Array.Empty<string>()));
    }

    [Fact]
    public void GroupSidInAllowedSids_IsAllowed()
    {
        Assert.True(Authorization.IsAllowed(ItemWith(Group), User, new[] { Group }));
    }

    [Fact]
    public void NeitherUserNorGroup_IsDenied()
    {
        Assert.False(Authorization.IsAllowed(ItemWith("S-1-5-21-1-2-3-500"), User, new[] { Group }));
    }

    [Fact]
    public void EmptyAllowedSids_IsDenied()
    {
        Assert.False(Authorization.IsAllowed(ItemWith(), User, new[] { Group }));
    }
}
