using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using RunAsLauncher.Ipc;
using RunAsLauncher.Policies;

namespace RunAsLauncher.IntegrationTests;

/// <summary>
/// Prepara uma política de teste em <c>%ProgramData%\RunAsLauncher</c> (com backup e restauração da atual)
/// e espera o serviço recarregá-la. Exige admin e o serviço <c>RunAsLauncher</c> rodando.
/// </summary>
public sealed class ServiceFixture : IDisposable
{
    public const string WhoamiPath = @"C:\Windows\System32\whoami.exe";
    public const string CmdPath = @"C:\Windows\System32\cmd.exe";

    /// <summary>Saída dos itens cmd (pasta de teste; o processo elevado grava aqui).</summary>
    public static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RunAsLauncherTest");

    public static readonly string GroupsFile = Path.Combine(OutputDir, "groups.txt");
    public static readonly string NoChildFile = Path.Combine(OutputDir, "nochild.txt");

    public readonly string TempCopy = Path.Combine(Path.GetTempPath(), "ral-whoami.exe");

    private readonly PolicyStore _store = PolicyStore.CreateDefault();
    private readonly byte[]? _backup;

    public ServiceFixture()
    {
        if (File.Exists(_store.FilePath))
        {
            _backup = File.ReadAllBytes(_store.FilePath);
        }

        Directory.CreateDirectory(OutputDir);
        File.Delete(GroupsFile);
        File.Delete(NoChildFile);
        File.Copy(WhoamiPath, TempCopy, overwrite: true);

        string userSid = WindowsIdentity.GetCurrent().User!.Value;
        string whoamiHash = Hash(WhoamiPath);
        string cmdHash = Hash(CmdPath);

        var policy = new Policy(PolicyValidator.CurrentSchemaVersion, new[]
        {
            Item("whoami", WhoamiPath, whoamiHash, userSid),
            Item("whoami-bad-hash", WhoamiPath, new string('0', 64), userSid),
            Item("whoami-temp", TempCopy, whoamiHash, userSid),
            Item("whoami-other-sid", WhoamiPath, whoamiHash, "S-1-5-32-551"), // Backup Operators
            Item("cmd-groups", CmdPath, cmdHash, userSid) with
            {
                Arguments = $"/c whoami /groups > \"{GroupsFile}\"",
            },
            Item("cmd-nochild", CmdPath, cmdHash, userSid) with
            {
                Arguments = $"/c whoami > \"{NoChildFile}\"",
                Children = ChildProcessPolicy.Deny,
            },
        });
        _store.Save(policy);

        // Debounce de 500 ms do serviço + folga.
        Thread.Sleep(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_backup is not null)
        {
            File.WriteAllBytes(_store.FilePath, _backup);
        }
        else
        {
            File.Delete(_store.FilePath);
        }

        File.Delete(TempCopy);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static PolicyItem Item(string id, string path, string sha256, string userSid) => new(
        id, id, path, Arguments: null, WorkingDirectory: null,
        RunMode.Elevated, WindowMode.Hidden, ChildProcessPolicy.Allow,
        sha256, Publisher: null, ExpectedSize: null, RequireProtectedLocation: true,
        AllowedSids: new[] { userSid }, IconPath: null);
}

[Trait("Category", "RequiresAdmin")]
public sealed class ServiceEndToEndTests : IClassFixture<ServiceFixture>
{
    private static async Task<string?> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    string text = await File.ReadAllTextAsync(path);
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
            }
            catch (IOException)
            {
                // ainda sendo escrito pelo processo lançado
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static async Task<Response> SendAsync(Request request)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", PipeNames.Main, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000);
        await Framing.WriteAsync(pipe, request);
        return await Framing.ReadAsync<Response>(pipe);
    }

    [Fact]
    public async Task Ping_ReturnsOk()
    {
        var response = await SendAsync(new Request(1, RequestType.Ping, null));
        Assert.Equal(ResultCode.Ok, response.Code);
    }

    [Fact]
    public async Task List_ReturnsAllowedItems()
    {
        var response = await SendAsync(new Request(1, RequestType.List, null));
        Assert.Equal(ResultCode.Ok, response.Code);
        Assert.NotNull(response.Items);
        Assert.Contains(response.Items!, i => i.Id == "whoami");
        Assert.Contains(response.Items!, i => i.Id == "whoami-bad-hash");
    }

    [Fact]
    public async Task Run_WithRealHash_LaunchesProcess()
    {
        var response = await SendAsync(new Request(1, RequestType.Run, "whoami"));
        Assert.True(response.Code == ResultCode.Ok, $"{response.Code}: {response.Message}");
        Assert.True(response.ProcessId > 0);
    }

    [Fact]
    public async Task Run_WithWrongHash_IsVerificationFailed()
    {
        var response = await SendAsync(new Request(1, RequestType.Run, "whoami-bad-hash"));
        Assert.Equal(ResultCode.VerificationFailed, response.Code);
        Assert.Null(response.ProcessId);
    }

    [Fact]
    public async Task Run_UnknownItem_IsNotFound()
    {
        var response = await SendAsync(new Request(1, RequestType.Run, "does-not-exist"));
        Assert.Equal(ResultCode.NotFound, response.Code);
    }

    [Fact]
    public async Task List_HidesItemsOfOtherSids()
    {
        var response = await SendAsync(new Request(1, RequestType.List, null));
        Assert.DoesNotContain(response.Items!, i => i.Id == "whoami-other-sid");
    }

    [Fact]
    public async Task Run_NotInAllowedSids_IsDenied()
    {
        var response = await SendAsync(new Request(1, RequestType.Run, "whoami-other-sid"));
        Assert.Equal(ResultCode.Denied, response.Code);
    }

    [Fact]
    public async Task Run_TargetInTemp_IsUnprotectedLocation()
    {
        var response = await SendAsync(new Request(1, RequestType.Run, "whoami-temp"));
        Assert.Equal(ResultCode.UnprotectedLocation, response.Code);
    }

    [Fact]
    public async Task Run_Elevated_HasAdministratorsAndHighIntegrity()
    {
        var response = await SendAsync(new Request(1, RequestType.Run, "cmd-groups"));
        Assert.True(response.Code == ResultCode.Ok, $"{response.Code}: {response.Message}");

        string? groups = await WaitForFileAsync(ServiceFixture.GroupsFile, TimeSpan.FromSeconds(10));
        Assert.NotNull(groups);
        Assert.Contains("S-1-16-12288", groups);  // High Mandatory Level
        Assert.Contains("S-1-5-32-544", groups);  // BUILTIN\Administrators
    }

    [Fact]
    public async Task Run_ChildrenDeny_BlocksChildProcess()
    {
        var response = await SendAsync(new Request(1, RequestType.Run, "cmd-nochild"));
        Assert.True(response.Code == ResultCode.Ok, $"{response.Code}: {response.Message}");

        // O cmd cria o arquivo do redirecionamento, mas o whoami (filho) não pode nascer: fica vazio.
        string? output = await WaitForFileAsync(ServiceFixture.NoChildFile, TimeSpan.FromSeconds(5));
        Assert.Null(output);
    }
}
