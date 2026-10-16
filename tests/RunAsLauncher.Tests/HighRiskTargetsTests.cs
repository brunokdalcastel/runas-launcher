using RunAsLauncher.Policies;

namespace RunAsLauncher.Tests;

public sealed class HighRiskTargetsTests
{
    private static PolicyItem Item(string path, RunMode mode = RunMode.Elevated,
                                   ChildProcessPolicy children = ChildProcessPolicy.Deny) =>
        new("item", "Item", path, null, null, mode, WindowMode.Normal, children,
            new string('A', 64), null, null, true, ["S-1-5-32-545"], null);

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\CMD.EXE")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\PowerShell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Windows\regedit.exe")]
    [InlineData(@"C:\Windows\System32\MMC.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Windows\System32\Taskmgr.exe")]
    [InlineData("cmd.exe")]
    public void ListedFileNames_AreHighRisk_CaseInsensitive(string path)
    {
        Assert.True(HighRiskTargets.IsHighRiskFile(path));
        Assert.True(HighRiskTargets.IsHighRisk(Item(path, RunMode.Standard)));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\cmd.exe.bak")]
    [InlineData(@"C:\Tools\mycmd.exe")]
    [InlineData(@"C:\cmd.exe\app.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherFileNames_AreNotHighRisk(string? path)
    {
        Assert.False(HighRiskTargets.IsHighRiskFile(path));
    }

    [Fact]
    public void ElevatedWithChildrenAllowed_IsHighRisk()
    {
        Assert.True(HighRiskTargets.IsHighRisk(Item(@"C:\Program Files\App\app.exe", RunMode.Elevated, ChildProcessPolicy.Allow)));
    }

    [Theory]
    [InlineData(RunMode.Elevated, ChildProcessPolicy.Deny)]
    [InlineData(RunMode.Elevated, ChildProcessPolicy.KillTreeOnExit)]
    [InlineData(RunMode.Standard, ChildProcessPolicy.Allow)]
    [InlineData(RunMode.Restricted, ChildProcessPolicy.Allow)]
    public void OtherModeCombinations_AreNotHighRisk(RunMode mode, ChildProcessPolicy children)
    {
        Assert.False(HighRiskTargets.IsHighRisk(Item(@"C:\Program Files\App\app.exe", mode, children)));
    }
}
