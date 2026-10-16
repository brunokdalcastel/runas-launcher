using System.Security.Principal;
using RunAsLauncher.Verification;

namespace RunAsLauncher.Tests;

public sealed class ProtectedLocationCheckerTests
{
    private static string CurrentUserSid =>
        WindowsIdentity.GetCurrent().User?.Value ?? "S-1-5-21-0-0-0-1000";

    [Fact]
    public void System32Executable_IsProtected()
    {
        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Assert.True(File.Exists(target), $"Pré-condição: {target} deve existir.");

        var (ok, offending) = ProtectedLocationChecker.Check(
            target, CurrentUserSid, Array.Empty<string>());

        Assert.True(ok, $"Esperado protegido; caminho ofensivo: {offending}");
        Assert.Null(offending);
    }

    [Fact]
    public void UserTempFile_IsNotProtected()
    {
        string target = Path.Combine(Path.GetTempPath(), $"ral-loc-{Guid.NewGuid():N}.exe");
        File.WriteAllText(target, "x");
        try
        {
            var (ok, offending) = ProtectedLocationChecker.Check(
                target, CurrentUserSid, Array.Empty<string>());

            Assert.False(ok);
            Assert.NotNull(offending);
        }
        finally
        {
            File.Delete(target);
        }
    }
}
