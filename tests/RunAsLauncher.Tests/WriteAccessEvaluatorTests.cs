using RunAsLauncher.Verification;

namespace RunAsLauncher.Tests;

public sealed class WriteAccessEvaluatorTests
{
    private const string UsersSid = "S-1-5-32-545";
    private const int ReadExecute = 0x20 | 0x1; // execute + read data
    private const int WriteData = WriteAccessEvaluator.FILE_WRITE_DATA;

    private static HashSet<string> Risky(params string[] sids) =>
        new(sids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void UsersWithWrite_IsWritable()
    {
        var aces = new[] { new AceInfo(UsersSid, Allow: true, Rights: WriteData, InheritOnly: false) };
        Assert.True(WriteAccessEvaluator.IsWritableBy(aces, Risky(UsersSid)));
    }

    [Fact]
    public void UsersReadExecuteOnly_IsNotWritable()
    {
        var aces = new[] { new AceInfo(UsersSid, Allow: true, Rights: ReadExecute, InheritOnly: false) };
        Assert.False(WriteAccessEvaluator.IsWritableBy(aces, Risky(UsersSid)));
    }

    [Fact]
    public void DenyCoversAllow_IsNotWritable()
    {
        var aces = new[]
        {
            new AceInfo(UsersSid, Allow: true, Rights: WriteData, InheritOnly: false),
            new AceInfo(UsersSid, Allow: false, Rights: WriteData, InheritOnly: false),
        };
        Assert.False(WriteAccessEvaluator.IsWritableBy(aces, Risky(UsersSid)));
    }

    [Fact]
    public void InheritOnlyWriteAce_IsIgnored()
    {
        var aces = new[] { new AceInfo(UsersSid, Allow: true, Rights: WriteData, InheritOnly: true) };
        Assert.False(WriteAccessEvaluator.IsWritableBy(aces, Risky(UsersSid)));
    }

    [Fact]
    public void WriteByNonRiskySid_IsIgnored()
    {
        var aces = new[] { new AceInfo("S-1-5-32-544", Allow: true, Rights: WriteData, InheritOnly: false) };
        Assert.False(WriteAccessEvaluator.IsWritableBy(aces, Risky(UsersSid)));
    }

    [Fact]
    public void PartialDeny_LeavesOtherWriteBit_IsWritable()
    {
        // Allow concede WRITE_DATA + DELETE; Deny cobre só DELETE → ainda sobra WRITE_DATA.
        int allowBits = WriteAccessEvaluator.FILE_WRITE_DATA | WriteAccessEvaluator.DELETE;
        var aces = new[]
        {
            new AceInfo(UsersSid, Allow: true, Rights: allowBits, InheritOnly: false),
            new AceInfo(UsersSid, Allow: false, Rights: WriteAccessEvaluator.DELETE, InheritOnly: false),
        };
        Assert.True(WriteAccessEvaluator.IsWritableBy(aces, Risky(UsersSid)));
    }
}
