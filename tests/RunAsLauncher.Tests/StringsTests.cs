using RunAsLauncher.Ipc;

namespace RunAsLauncher.Tests;

public sealed class StringsTests
{
    public static TheoryData<ResultCode> AllResultCodes()
    {
        var data = new TheoryData<ResultCode>();
        foreach (ResultCode code in Enum.GetValues<ResultCode>())
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllResultCodes))]
    public void EveryResultCode_HasPortugueseAndEnglishText(ResultCode code)
    {
        var (pt, en) = Strings.ResultTexts(code);

        Assert.False(string.IsNullOrWhiteSpace(pt));
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.NotEqual(pt, en);
    }

    [Fact]
    public void ResultTexts_AreDistinctPerCode()
    {
        var codes = Enum.GetValues<ResultCode>();

        Assert.Equal(codes.Length, codes.Select(c => Strings.ResultTexts(c).Pt).Distinct().Count());
        Assert.Equal(codes.Length, codes.Select(c => Strings.ResultTexts(c).En).Distinct().Count());
    }

    [Fact]
    public void Describe_AppendsDetailOnlyWhenPresent()
    {
        Assert.Equal(Strings.ForResult(ResultCode.LaunchFailed), Strings.Describe(ResultCode.LaunchFailed, null));
        Assert.Contains("erro 5", Strings.Describe(ResultCode.LaunchFailed, "erro 5"));
    }
}
