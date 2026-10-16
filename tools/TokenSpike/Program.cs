using System.ComponentModel;
using System.Runtime.InteropServices;
using RunAsLauncher.Elevation;
using RunAsLauncher.Identity;
using RunAsLauncher.Native;

// Spike da Fase 1: valida, rodando como LocalSystem, a criação de um token elevado do próprio
// usuário via S4U (sem senha) e o lançamento de um processo na área de trabalho do usuário.
// Uso: TokenSpike.exe <usuario> <dominio|.> <local|domain>

const string OutputDir = @"C:\ProgramData\RunAsLauncherSpike";
Directory.CreateDirectory(OutputDir);
string outPath = Path.Combine(OutputDir, "result.txt");
var log = new List<string> { $"[spike] {DateTime.Now:O}", $"WhoAmI(processo)={System.Security.Principal.WindowsIdentity.GetCurrent().Name}" };

try
{
    if (args.Length < 3)
    {
        throw new ArgumentException("Uso: TokenSpike.exe <usuario> <dominio|.> <local|domain>");
    }

    string user = args[0];
    string domain = args[1] == "." ? Environment.MachineName : args[1];
    AccountKind kind = args[2].Equals("domain", StringComparison.OrdinalIgnoreCase) ? AccountKind.Domain : AccountKind.Local;

    uint sessionId = Kernel32.WTSGetActiveConsoleSessionId();
    log.Add($"Entrada: user={user} domain={domain} kind={kind} sessionAtiva={sessionId}");

    var factory = new TokenFactory();
    using SafeKernelHandle token = factory.CreateElevatedUserToken(user, domain, kind, sessionId, out var report);
    log.Add($"ElevationType(S4U)={report.ElevationType} usouLinkedToken={report.UsedLinkedToken} forcouIntegridade={report.ForcedIntegrity}");

    log.Add($"IntegridadeTokenFinal={DescribeIntegrity(ReadIntegritySid(token))}");

    string whoamiOut = Path.Combine(OutputDir, "whoami.txt");
    File.Delete(whoamiOut);
    string args2 = $"/c whoami /all > \"{whoamiOut}\" 2>&1";
    int pid = LaunchOnUserDesktop(token, @"C:\Windows\System32\cmd.exe", args2, sessionId);
    log.Add($"cmd.exe (whoami /all → whoami.txt) lançado na sessão {sessionId}, PID={pid}");
    System.Threading.Thread.Sleep(1500);
    log.Add(File.Exists(whoamiOut) ? "whoami.txt gerado — veja os grupos e o Mandatory Label nele." : "whoami.txt NÃO foi gerado.");
    log.Add("RESULTADO: sucesso (verificar whoami.txt: Administrators Enabled e High Mandatory Level).");
}
catch (Exception ex)
{
    log.Add($"RESULTADO: FALHA — {ex.GetType().Name}: {ex.Message}");
}

File.WriteAllLines(outPath, log);
Console.WriteLine(string.Join(Environment.NewLine, log));
return;

static string ReadIntegritySid(SafeKernelHandle token)
{
    Advapi32.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out uint needed);
    IntPtr buffer = Marshal.AllocHGlobal((int)needed);
    try
    {
        if (!Advapi32.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, needed, out _))
        {
            return "erro";
        }

        IntPtr sidPtr = Marshal.ReadIntPtr(buffer);
        return new System.Security.Principal.SecurityIdentifier(sidPtr).Value;
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static string DescribeIntegrity(string sid) => sid switch
{
    "S-1-16-12288" => "High (S-1-16-12288)",
    "S-1-16-8192" => "Medium (S-1-16-8192)",
    "S-1-16-16384" => "System (S-1-16-16384)",
    _ => sid,
};

static int LaunchOnUserDesktop(SafeKernelHandle token, string app, string arguments, uint sessionId)
{
    if (!Userenv.CreateEnvironmentBlock(out SafeEnvironmentBlock env, token, inherit: false))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock falhou");
    }

    using (env)
    {
        IntPtr desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        try
        {
            var startup = new STARTUPINFOEXW();
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOW>();
            startup.StartupInfo.lpDesktop = desktop;

            string commandLine = $"\"{app}\" {arguments}";
            if (!Advapi32.CreateProcessAsUserW(
                    token, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    Kernel32.CREATE_UNICODE_ENVIRONMENT | Kernel32.CREATE_NEW_CONSOLE,
                    env.DangerousGetHandle(), Path.GetDirectoryName(app),
                    ref startup, out PROCESS_INFORMATION pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser falhou");
            }

            Kernel32.CloseHandle(pi.hThread);
            Kernel32.CloseHandle(pi.hProcess);
            return (int)pi.dwProcessId;
        }
        finally
        {
            Marshal.FreeHGlobal(desktop);
        }
    }
}
