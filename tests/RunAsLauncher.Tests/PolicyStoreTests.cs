using RunAsLauncher.Policies;

namespace RunAsLauncher.Tests;

public sealed class PolicyStoreTests : IDisposable
{
    private const string ValidHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    private readonly string _dir;
    private readonly PolicyStore _store;

    public PolicyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "RALTests-" + Guid.NewGuid().ToString("N"));
        _store = new PolicyStore(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpeza best-effort.
        }
    }

    private static Policy SamplePolicy() => new(1, new[]
    {
        new PolicyItem(
            Id: "notepad",
            Title: "Notepad",
            TargetPath: @"C:\Windows\System32\notepad.exe",
            Arguments: "/A",
            WorkingDirectory: null,
            Mode: RunMode.Elevated,
            Window: WindowMode.Normal,
            Children: ChildProcessPolicy.Deny,
            Sha256: ValidHash,
            Publisher: null,
            ExpectedSize: 123456,
            RequireProtectedLocation: true,
            AllowedSids: new[] { "S-1-5-21-111-222-333-1001" },
            IconPath: null),
    });

    // Record equality compara as listas (Items, AllowedSids) por referência, então um round-trip
    // nunca é "igual" ao original; comparamos campo a campo.
    private static void AssertSamePolicy(Policy expected, Policy? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.SchemaVersion, actual!.SchemaVersion);
        Assert.Equal(expected.Items.Count, actual.Items.Count);
        for (int i = 0; i < expected.Items.Count; i++)
        {
            var e = expected.Items[i];
            var a = actual.Items[i];
            Assert.Equal(e with { AllowedSids = Array.Empty<string>() },
                         a with { AllowedSids = Array.Empty<string>() });
            Assert.Equal(e.AllowedSids, a.AllowedSids);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var policy = SamplePolicy();
        _store.Save(policy);

        var result = _store.Load();

        Assert.True(result.Success);
        AssertSamePolicy(policy, result.Policy);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyPolicy()
    {
        var result = _store.Load();

        Assert.True(result.Success);
        Assert.NotNull(result.Policy);
        Assert.Empty(result.Policy!.Items);
    }

    [Fact]
    public void Load_CorruptJson_IsRejected()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.FilePath, "{ this is not json ");

        var result = _store.Load();

        Assert.False(result.Success);
        Assert.Null(result.Policy);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Load_UnknownSchema_IsRejected()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.FilePath, "{ \"schemaVersion\": 999, \"items\": [] }");

        var result = _store.Load();

        Assert.False(result.Success);
        Assert.Contains("SCHEMA_VERSION", result.Reason);
    }

    [Fact]
    public void Load_InvalidItem_IsRejected()
    {
        // Item sem hash e sem publisher: validação reprova → Rejected (nega tudo).
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.FilePath,
            "{ \"schemaVersion\": 1, \"items\": [ { \"id\": \"x\", \"title\": \"X\", " +
            "\"targetPath\": \"C:\\\\Windows\\\\System32\\\\notepad.exe\", \"mode\": \"standard\", " +
            "\"window\": \"normal\", \"children\": \"allow\", \"requireProtectedLocation\": true, " +
            "\"allowedSids\": [] } ] }");

        var result = _store.Load();

        Assert.False(result.Success);
        Assert.Contains("NO_HASH_OR_PUBLISHER", result.Reason);
    }

    [Fact]
    public void Save_InvalidPolicy_Throws()
    {
        var bad = new Policy(1, new[]
        {
            SamplePolicy().Items[0] with { Sha256 = null, Publisher = null },
        });

        var ex = Assert.Throws<PolicyValidationException>(() => _store.Save(bad));
        Assert.False(ex.Result.IsValid);
    }

    [Fact]
    public void ExportThenImport_RoundTrips()
    {
        var policy = SamplePolicy();
        var path = Path.Combine(_dir, "export", "policy-export.json");

        _store.Export(path, policy);
        var result = _store.Import(path);

        Assert.True(result.Success);
        AssertSamePolicy(policy, result.Policy);
    }

    [Fact]
    public void EnumsAreSerializedAsCamelCaseStrings()
    {
        _store.Save(SamplePolicy());
        var json = File.ReadAllText(_store.FilePath);

        Assert.Contains("\"elevated\"", json);
        Assert.Contains("\"deny\"", json);
        Assert.DoesNotContain("\"Mode\":", json); // PascalCase não deve aparecer
    }
}
