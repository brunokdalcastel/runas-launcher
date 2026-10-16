using System.Security.Principal;
using RunAsLauncher.Identity;

namespace RunAsLauncher.Tests;

public sealed class AccountClassifierTests
{
    private static readonly SecurityIdentifier Machine = new("S-1-5-21-1-2-3");

    [Fact]
    public void LocalAccount_SameMachineDomain_IsLocal()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        Assert.Equal(AccountKind.Local, AccountClassifier.Classify(user, Machine));
    }

    [Fact]
    public void DomainAccount_DifferentDomain_IsDomain()
    {
        var user = new SecurityIdentifier("S-1-5-21-9-9-9-1001");
        Assert.Equal(AccountKind.Domain, AccountClassifier.Classify(user, Machine));
    }

    [Fact]
    public void EntraIdAccount_IsUnsupported()
    {
        var user = new SecurityIdentifier("S-1-12-1-1-2-3-4");
        Assert.Equal(AccountKind.Unsupported, AccountClassifier.Classify(user, Machine));
    }

    [Fact]
    public void WellKnownServiceSid_IsUnsupported()
    {
        var user = new SecurityIdentifier("S-1-5-18"); // LocalSystem
        Assert.Equal(AccountKind.Unsupported, AccountClassifier.Classify(user, Machine));
    }
}
