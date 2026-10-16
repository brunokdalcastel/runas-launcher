# Spec: RunAs Launcher v1

> Base: [`docs/PRD.md`](PRD.md) (decisões Q1–Q5 de 2026-10-16). Executar fase a fase com `/implement N`.
> Raiz do repositório: `C:\Users\Usuario\runas-launcher`. Namespace raiz: `RunAsLauncher`.
> Repositório **público** (desde 2026-10-16; antes privado), licença proprietária. Commits pequenos, **sem** linhas `Co-Authored-By`.

## Convenções globais (valem para todas as fases)

- TFM `net10.0-windows10.0.19041.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`,
  `AllowUnsafeBlocks=true`, `DebugType=embedded` (padrão Fragmo).
- P/Invoke: `internal static partial class` + `[LibraryImport]`, `SetLastError = true`; handles em `SafeHandle`
  (padrão `src/Fragmo/Native/User32.cs`). Falha nativa → `throw new Win32Exception(Marshal.GetLastWin32Error())`.
- JSON: `System.Text.Json`, camelCase, enums como string (padrão `SettingsStore`), gravação atômica `.tmp` → `File.Move`.
- Lógica pura separada de I/O e de UI, para testes unitários sem admin.
- Textos visíveis via `Strings.T(pt, en)` (padrão `src/Fragmo/Strings.cs`).
- Event IDs fixos (fonte `RunAsLauncher`, log `Application`):
  `1000` lançado · `1001` negado · `1002` erro de lançamento · `1100` política carregada · `1101` política rejeitada ·
  `1200` serviço iniciado · `1201` serviço parado.

---

## Phase 1: Setup da solução + spike de token (risco R1)

Objetivo: criar o esqueleto e **provar** que um serviço SYSTEM consegue gerar um token elevado (integridade High)
para o usuário logado sem senha. Se o spike falhar, parar e revisar o PRD antes da Fase 2.

### `.gitignore`
- Action: [NEW]
- Instruction: padrão .NET (`bin/`, `obj/`, `artifacts/`, `*.user`, `.vs/`, `TestResults/`).

### `nuget.config`
- Action: [NEW]
- Instruction: copiar de `C:\Users\Usuario\fragmo\nuget.config` (só nuget.org).

### `Directory.Build.props`
- Action: [NEW]
- Instruction: centralizar as propriedades das convenções globais + `<Version>0.1.0</Version>`,
  `<Product>RunAs Launcher</Product>`, `<Company>Bruno Koch Dal Castel</Company>`.

### `RunAsLauncher.sln`
- Action: [NEW]
- Instruction: `dotnet new sln`; adicionar todos os projetos abaixo conforme forem criados.

### `src/RunAsLauncher.Core/RunAsLauncher.Core.csproj`
- Action: [NEW]
- Instruction: `classlib`. Pacotes: `System.IO.FileSystem.AccessControl` só se necessário (no .NET 10 Windows já
  vem `FileSystemAclExtensions`). `InternalsVisibleTo` para `RunAsLauncher.Tests` e `RunAsLauncher.Service`.

### `src/RunAsLauncher.Core/Native/SafeHandles.cs`
- Action: [NEW]
- Instruction: `SafeKernelHandle` (`CloseHandle`), `SafeLsaHandle` (`LsaDeregisterLogonProcess`),
  `SafeLsaBuffer` (`LsaFreeReturnBuffer`), `SafeEnvironmentBlock` (`DestroyEnvironmentBlock`),
  `SafeProcThreadAttributeList` (`DeleteProcThreadAttributeList` + `Marshal.FreeHGlobal`).

### `src/RunAsLauncher.Core/Native/Secur32.cs`
- Action: [NEW]
- Instruction: `LsaRegisterLogonProcess`, `LsaLookupAuthenticationPackage`, `LsaLogonUser`,
  `LsaFreeReturnBuffer`, `LsaDeregisterLogonProcess`, `LsaNtStatusToWinError` (advapi32).
  Structs: `LSA_STRING`, `UNICODE_STRING`, `TOKEN_SOURCE`, `QUOTA_LIMITS`, `MSV1_0_S4U_LOGON`
  (`MessageType = MsV1_0S4ULogon = 12`), `KERB_S4U_LOGON` (`MessageType = KerbS4ULogon = 12`),
  `TOKEN_GROUPS` com 1 `SID_AND_ATTRIBUTES`.
  Os buffers `UNICODE_STRING` de usuário/domínio devem ficar **contíguos** à struct (exigência da doc):
  alocar um único bloco `Marshal.AllocHGlobal(sizeof(struct) + bytes das strings)` e apontar os `Buffer` para dentro dele.
- *Snippet:*
```csharp
[LibraryImport("secur32.dll")]
public static partial int LsaLogonUser(
    SafeLsaHandle lsaHandle, ref LSA_STRING originName, SECURITY_LOGON_TYPE logonType,
    uint authenticationPackage, IntPtr authenticationInformation, uint authenticationInformationLength,
    IntPtr localGroups, ref TOKEN_SOURCE sourceContext,
    out IntPtr profileBuffer, out uint profileBufferLength, out LUID logonId,
    out IntPtr token, out QUOTA_LIMITS quotas, out int subStatus);
```

### `src/RunAsLauncher.Core/Native/Advapi32.cs`
- Action: [NEW]
- Instruction: `OpenProcessToken`, `GetTokenInformation`, `SetTokenInformation`, `DuplicateTokenEx`,
  `CreateProcessAsUserW`, `CreateRestrictedToken`, `ConvertStringSidToSidW`, `AdjustTokenPrivileges`,
  `LookupPrivilegeValueW`. Enums `TOKEN_INFORMATION_CLASS` (incluir `TokenElevationType=18`, `TokenLinkedToken=19`,
  `TokenElevation=20`, `TokenIntegrityLevel=25`, `TokenSessionId=12`), `TOKEN_ELEVATION_TYPE`.

### `src/RunAsLauncher.Core/Native/Kernel32.cs`, `Userenv.cs`, `Wtsapi32.cs`
- Action: [NEW]
- Instruction: `CloseHandle`, `InitializeProcThreadAttributeList`, `UpdateProcThreadAttribute`,
  `CreateJobObjectW`, `SetInformationJobObject`, `GetNamedPipeClientProcessId`, `GetNamedPipeClientSessionId`,
  `ProcessIdToSessionId`; `CreateEnvironmentBlock`, `DestroyEnvironmentBlock`; `WTSQueryUserToken`,
  `WTSGetActiveConsoleSessionId`. Structs `STARTUPINFOEXW`, `PROCESS_INFORMATION`,
  `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`.

### `src/RunAsLauncher.Core/Elevation/TokenFactory.cs`
- Action: [NEW]
- Instruction: classe `TokenFactory` com
  `SafeKernelHandle CreateElevatedUserToken(string user, string domain, AccountKind kind, int sessionId)`:
  1. habilita `SeTcbPrivilege` no processo;
  2. `LsaRegisterLogonProcess("RunAsLauncher")`;
  3. pacote `MICROSOFT_AUTHENTICATION_PACKAGE_V1_0` para `AccountKind.Local`, `Kerberos` para `AccountKind.Domain`;
  4. `LsaLogonUser(LogonType = Batch, S4U, LocalGroups = [S-1-5-32-544 com SE_GROUP_ENABLED|SE_GROUP_ENABLED_BY_DEFAULT|SE_GROUP_MANDATORY])`;
  5. `TokenElevationType == Limited` → trocar pelo `TokenLinkedToken`;
  6. se a integridade ainda for < High → `SetTokenInformation(TokenIntegrityLevel, S-1-16-12288)`;
  7. `DuplicateTokenEx(TokenPrimary)` + `SetTokenInformation(TokenSessionId, sessionId)`.
  Estratégia final (passos 5/6 necessários ou não) é fixada pelo resultado do spike.

### `tools/TokenSpike/TokenSpike.csproj` + `tools/TokenSpike/Program.cs`
- Action: [NEW]
- Instruction: console app que referencia o Core. Uso: `TokenSpike.exe <usuario> <dominio|.> <local|domain>`.
  Pega a sessão ativa (`WTSGetActiveConsoleSessionId`), chama `TokenFactory`, imprime em
  `C:\ProgramData\RunAsLauncherSpike\result.txt`: elevation type, integrity SID, grupos, sessionId, e então lança
  `cmd.exe /k whoami /groups` na sessão do usuário com `CreateProcessAsUserW` (`lpDesktop = "winsta0\\default"`).
  Não entra na solução publicada.

### `docs/Spike-Token.md`
- Action: [NEW]
- Instruction: registrar como rodar o spike como SYSTEM
  (`schtasks /create /tn RALSpike /sc once /st 00:00 /ru SYSTEM /tr "...\TokenSpike.exe bruno . local"` + `schtasks /run /tn RALSpike`),
  os resultados obtidos (conta local; conta MSA-linked; conta AD se houver) e a estratégia escolhida para os passos 5/6.

### Verification
- [x] `dotnet build` sem warnings (solução é `RunAsLauncher.slnx`; .NET 10 gera `.slnx`). 0 warnings, 0 errors.
- [x] Spike rodado como SYSTEM (2026-10-16): `whoami` = próprio usuário; `BUILTIN\Administradores` = Enabled;
      Mandatory Label = High (S-1-16-12288). Token S4U já veio não-filtrado (sem LinkedToken/integridade forçada).
- [x] `result.txt` + `whoami.txt` registram elevation type e integridade; `docs/Spike-Token.md` atualizado com a estratégia.
- [ ] **(a validar em VM, antes do release)** conta padrão pura e conta de domínio AD — ver `docs/Test-Plan.md` (Fase 9).

---

## Phase 2: Modelo de política (Core, lógica pura)

### `src/RunAsLauncher.Core/Policy/PolicyItem.cs`
- Action: [NEW]
- Instruction: records imutáveis.
- *Snippet:*
```csharp
public enum RunMode { Elevated, Standard, Restricted }
public enum WindowMode { Normal, Hidden, Minimized, Maximized }
public enum ChildProcessPolicy { Allow, Deny, KillTreeOnExit }

public sealed record PolicyItem(
    string Id,                       // slug [a-z0-9-]{1,40}, único
    string Title,
    string TargetPath,               // caminho absoluto local, sem variáveis de ambiente do usuário
    string? Arguments,               // fixos; o cliente nunca envia argumentos
    string? WorkingDirectory,        // null = pasta do alvo
    RunMode Mode,
    WindowMode Window,
    ChildProcessPolicy Children,
    string? Sha256,                  // hex maiúsculo; null só se Publisher != null
    PublisherRule? Publisher,
    long? ExpectedSize,
    bool RequireProtectedLocation,   // F4, default true
    IReadOnlyList<string> AllowedSids,  // usuários/grupos; vazio = ninguém
    string? IconPath);

public sealed record PublisherRule(string SubjectContains, string? Thumbprint);

public sealed record Policy(int SchemaVersion, IReadOnlyList<PolicyItem> Items);
```

### `src/RunAsLauncher.Core/Policy/PolicyValidator.cs`
- Action: [NEW]
- Instruction: `ValidationResult Validate(Policy p)` retornando lista de erros com `itemId` + código. Regras:
  `SchemaVersion == 1`; ids únicos e no formato slug; `TargetPath` absoluto, termina em `.exe`, não UNC (v1);
  `Sha256` 64 hex **ou** `Publisher` presente (nunca os dois nulos); `AllowedSids` só SIDs válidos (`S-1-...`)
  e nunca `S-1-1-0` (Everyone) sem aviso; `WorkingDirectory` absoluto se presente; máx. 500 itens.

### `src/RunAsLauncher.Core/Policy/PolicyStore.cs`
- Action: [NEW]
- Instruction: igual ao `SettingsStore` do Fragmo (atômico, camelCase), com diferenças:
  `Load()` retorna `PolicyLoadResult` (`Ok(Policy)` | `Rejected(reason)`); JSON inválido ou validação falha →
  `Rejected` (**negar tudo**, nunca política padrão permissiva). Caminho padrão
  `%ProgramData%\RunAsLauncher\policy.json`. `Save(Policy)` valida antes de gravar. `Export/Import(path)`.

### `tests/RunAsLauncher.Tests/RunAsLauncher.Tests.csproj`
- Action: [NEW]
- Instruction: xUnit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.runner.visualstudio` 3.1.4,
  `<Using Include="Xunit" />`, referência ao Core.

### `tests/RunAsLauncher.Tests/PolicyValidatorTests.cs`, `PolicyStoreTests.cs`
- Action: [NEW]
- Instruction: casos: política válida; id duplicado; sem hash e sem publisher; hash malformado; caminho relativo;
  UNC; JSON corrompido → `Rejected`; round-trip save/load em pasta temporária; schema desconhecido → `Rejected`.

### Verification
- [x] `dotnet test` verde, todos os casos acima cobertos. (2026-10-16: 26 testes passam; `dotnet build` Release 0 warnings.)

---

## Phase 3: Verificação do alvo e do chamador (Core)

### `src/RunAsLauncher.Core/Verification/FileHasher.cs`
- Action: [NEW]
- Instruction: `string Sha256(FileStream fs)` → hex maiúsculo (`SHA256.HashData(stream)`), sem reabrir o arquivo.

### `src/RunAsLauncher.Core/Verification/LockedTarget.cs`
- Action: [NEW]
- Instruction: `IDisposable` que abre o alvo com `FileShare.Read` (sem Write/Delete) e mantém aberto até depois do
  `CreateProcessAsUser` (mitigação R4). Expõe `FileStream`, `Length`, caminho final normalizado
  (`GetFinalPathNameByHandle` — recusa se diferir do `TargetPath` após normalização, ex. via junction).

### `src/RunAsLauncher.Core/Verification/AuthenticodeVerifier.cs` + `Native/Wintrust.cs`
- Action: [NEW]
- Instruction: `WinVerifyTrust` com `WINTRUST_ACTION_GENERIC_VERIFY_V2`, `WTD_REVOKE_WHOLECHAIN` desligado por padrão
  (offline-friendly), retorno `TrustResult(valid, subject, thumbprint)`;
  publisher via `X509Certificate.CreateFromSignedFile`. `bool Matches(PublisherRule rule, TrustResult r)`.

### `src/RunAsLauncher.Core/Verification/WriteAccessEvaluator.cs`
- Action: [NEW]
- Instruction: lógica pura: `bool IsWritableBy(IEnumerable<AceInfo> aces, ISet<string> riskySids)`.
  `AceInfo(string Sid, bool Allow, int Rights, bool InheritOnly)`. Direitos de escrita considerados:
  `FILE_WRITE_DATA|FILE_APPEND_DATA|WRITE_DAC|WRITE_OWNER|DELETE|GENERIC_WRITE|GENERIC_ALL` e, nas pastas,
  `FILE_ADD_FILE|FILE_ADD_SUBDIRECTORY|FILE_DELETE_CHILD`. Ignorar ACEs `InheritOnly`.
  Deny ACE para o mesmo SID anula apenas os bits que ele cobre.

### `src/RunAsLauncher.Core/Verification/ProtectedLocationChecker.cs`
- Action: [NEW]
- Instruction: para o arquivo e **cada ancestral** até a raiz do volume, ler DACL (`FileSystemAclExtensions.GetAccessControl`),
  converter para `AceInfo` e chamar o avaliador com `riskySids` = {S-1-1-0 Everyone, S-1-5-11 Authenticated Users,
  S-1-5-32-545 Users, S-1-5-4 Interactive, SID do chamador + todos os grupos do chamador exceto Administrators/SYSTEM}.
  Também recusar se o dono (owner) for um SID arriscado. Retorna `(bool ok, string? offendingPath)`.

### `src/RunAsLauncher.Core/Identity/AccountClassifier.cs`
- Action: [NEW]
- Instruction: `AccountKind Classify(SecurityIdentifier userSid, SecurityIdentifier machineSid)`:
  `S-1-12-1-*` → `Unsupported` (Entra ID); `userSid.AccountDomainSid == machineSid` → `Local`;
  `S-1-5-21-*` com domínio diferente → `Domain`; resto → `Unsupported`. (Se o spike reprovou contas MSA-linked,
  detectar via `HKLM\SAM` não é possível sem SYSTEM — nesse caso o serviço tenta S4U e mapeia a falha para
  erro "conta não suportada".)

### `src/RunAsLauncher.Core/Identity/Authorization.cs`
- Action: [NEW]
- Instruction: lógica pura `bool IsAllowed(PolicyItem item, string userSid, IEnumerable<string> groupSids)`.

### `tests/RunAsLauncher.Tests/WriteAccessEvaluatorTests.cs`, `AccountClassifierTests.cs`, `AuthorizationTests.cs`, `FileHasherTests.cs`, `ProtectedLocationCheckerTests.cs`
- Action: [NEW]
- Instruction: avaliador com ACEs sintéticas (Users com Modify → gravável; Users só RX → ok; deny cobre allow;
  InheritOnly ignorado). Checker real: `C:\Windows\System32\notepad.exe` → ok; arquivo em `%TEMP%` → gravável.
  Hasher com arquivo de conteúdo conhecido. Classificador com SIDs de exemplo.

### Verification
- [x] `dotnet test` verde. (2026-10-16: 44 testes passam; `dotnet build` Release 0 warnings.)
- [x] Teste manual coberto por `ProtectedLocationCheckerTests`: aprova `C:\Windows\System32\notepad.exe`
      (e por extensão `C:\Program Files\...`) e reprova um `.exe` em `%TEMP%` (pasta do usuário).
      **Decisão (logic error no Spec):** máscara ciente do objeto — diretórios ancestrais usam
      `WriteAccessEvaluator.DirectoryWriteMask` (sem `FILE_ADD_FILE`/`FILE_ADD_SUBDIRECTORY`), porque `C:\`
      concede `ADD_SUBDIRECTORY` a *Authenticated Users* por padrão, o que reprovaria todo alvo sob `C:\`.

---

## Phase 4: Protocolo IPC (Core)

### `src/RunAsLauncher.Core/Ipc/Messages.cs`
- Action: [NEW]
- Instruction: DTOs **não polimórficos**, com campo `type` explícito.
- *Snippet:*
```csharp
public enum RequestType { List, Run, Ping }
public sealed record Request(int Version, RequestType Type, string? ItemId);
public sealed record ItemView(string Id, string Title, string TargetPath, RunMode Mode, string? IconPath);
public enum ResultCode { Ok, Denied, NotFound, VerificationFailed, UnprotectedLocation,
                         UnsupportedAccount, PolicyRejected, RateLimited, LaunchFailed, BadRequest }
public sealed record Response(int Version, ResultCode Code, string? Message,
                              IReadOnlyList<ItemView>? Items, int? ProcessId);
```

### `src/RunAsLauncher.Core/Ipc/Framing.cs`
- Action: [NEW]
- Instruction: `WriteAsync/ReadAsync(Stream, T, ct)`: prefixo de 4 bytes little-endian + JSON UTF-8;
  tamanho máximo **16 KiB** para requisição e 1 MiB para resposta; tamanho inválido → `InvalidDataException`.
  `ItemId` validado com o mesmo regex de slug.

### `src/RunAsLauncher.Core/Ipc/PipeNames.cs`
- Action: [NEW]
- Instruction: `public const string Main = "RunAsLauncher";`.

### `tests/RunAsLauncher.Tests/FramingTests.cs`
- Action: [NEW]
- Instruction: round-trip em `MemoryStream`; prefixo > limite; stream truncado; JSON com `type` desconhecido → erro.

### Verification
- [x] `dotnet test` verde.

---

## Phase 5: Serviço (broker SYSTEM)

### `src/RunAsLauncher.Service/RunAsLauncher.Service.csproj`
- Action: [NEW]
- Instruction: `worker` SDK (`Microsoft.NET.Sdk.Worker`), `OutputType=Exe`, `AssemblyName=RunAsLauncher.Service`.
  Pacotes: `Microsoft.Extensions.Hosting.WindowsServices` e `System.Diagnostics.EventLog` (10.0.x estáveis).
  Publish single-file self-contained igual ao Fragmo.

### `src/RunAsLauncher.Service/Program.cs`
- Action: [NEW]
- Instruction: `Host.CreateApplicationBuilder`; `AddWindowsService(o => o.ServiceName = "RunAsLauncher")`;
  registra `AuditLog`, `PolicyProvider`, `LaunchService`, `PipeServer` (HostedService).
  Argumentos de manutenção (usados pelo instalador, exigem admin): `--register-eventsource`, `--unregister-eventsource`.

### `src/RunAsLauncher.Service/AuditLog.cs`
- Action: [NEW]
- Instruction: wrapper de `EventLog` (fonte `RunAsLauncher`) com métodos por Event ID das convenções globais.
  Cada evento de lançamento inclui: usuário (DOMÍNIO\nome + SID), sessão, itemId, caminho, SHA-256 calculado,
  modo, resultado, PID.

### `src/RunAsLauncher.Service/PolicyProvider.cs`
- Action: [NEW]
- Instruction: carrega a política no start e a cada mudança (`FileSystemWatcher` com debounce de 500 ms).
  **Antes** de cada carga, verificar a ACL de `%ProgramData%\RunAsLauncher` e de `policy.json` com
  `WriteAccessEvaluator` (riskySids = Everyone, Authenticated Users, Users, Interactive): se gravável por não-admin →
  `Rejected` + evento 1101. Expõe `PolicyLoadResult Current` (thread-safe via `Volatile`).

### `src/RunAsLauncher.Service/PipeServer.cs`
- Action: [NEW]
- Instruction: `BackgroundService` com até 4 instâncias simultâneas.
  `NamedPipeServerStreamAcl.Create(PipeNames.Main, InOut, 4, Byte, Asynchronous | FirstPipeInstance, 0, 0, security)`;
  se `PipeOptions.FirstPipeInstance` não existir no .NET 10, criar via P/Invoke `CreateNamedPipeW` com
  `FILE_FLAG_FIRST_PIPE_INSTANCE` (anti pipe squatting, R5).
  `PipeSecurity`: SYSTEM e Administrators FullControl; Authenticated Users `ReadWrite` (sem `CreateNewInstance`).
  Por conexão: timeout de leitura 5 s; identidade do cliente via `pipe.RunAsClient(() => WindowsIdentity.GetCurrent())`
  (SID + grupos); sessão via `GetNamedPipeClientSessionId`; **rate limit** 20 req/min por SID; delega ao `LaunchService`.

### `src/RunAsLauncher.Service/LaunchService.cs`
- Action: [NEW]
- Instruction: fluxo de `Run(itemId, caller)`, falhando cedo com `ResultCode` e auditando sempre:
  1. política `Rejected` → `PolicyRejected`;
  2. item inexistente → `NotFound`; `Authorization.IsAllowed` falso → `Denied`;
  3. `AccountClassifier` → `Unsupported` → `UnsupportedAccount` (Entra ID);
  4. `ProtectedLocationChecker` se `RequireProtectedLocation` → `UnprotectedLocation`;
  5. `using LockedTarget`; tamanho, SHA-256 e/ou Authenticode → `VerificationFailed`;
  6. token: `Elevated` → `TokenFactory.CreateElevatedUserToken`; `Standard` → `WTSQueryUserToken(session)`;
     `Restricted` → `WTSQueryUserToken` + `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE | LUA_TOKEN)`;
  7. `SessionLauncher.Start(...)`; 8. evento 1000 com PID → `Ok`.
  `List(caller)` retorna só os itens que `IsAllowed` aprova.

### `src/RunAsLauncher.Service/Elevation/SessionLauncher.cs`
- Action: [NEW]
- Instruction: `int Start(SafeKernelHandle token, PolicyItem item)`:
  `CreateEnvironmentBlock(token, inherit: false)`; `STARTUPINFOEXW` com `lpDesktop = "winsta0\\default"`,
  `STARTF_USESHOWWINDOW` + `wShowWindow` conforme `WindowMode`; lista de atributos com
  `PROC_THREAD_ATTRIBUTE_JOB_LIST` (Job com `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` só se `KillTreeOnExit`; o handle
  do job fica num dicionário até o processo terminar) e `PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY = 0x01`
  se `Deny`. Flags: `EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE`.
  Linha de comando: `"\"" + TargetPath + "\" " + Arguments` (nunca vinda do cliente).
  Diretório: `WorkingDirectory ?? Path.GetDirectoryName(TargetPath)`. Fechar handles de thread/processo.

### `src/RunAsLauncher.Service/Elevation/TokenFactory.cs`
- Action: [MOD — mover]
- Instruction: mover `TokenFactory` do Core para o Service (só o serviço deve criar tokens elevados), mantendo a
  estratégia definida em `docs/Spike-Token.md`; atualizar `tools/TokenSpike` para referenciar o arquivo via `<Compile Include>`.

### `tests/RunAsLauncher.IntegrationTests/` (projeto + `ServiceEndToEndTests.cs`)
- Action: [NEW]
- Instruction: testes marcados `[Trait("Category","RequiresAdmin")]`, fora do `dotnet test` padrão
  (filtro em `publish.ps1`): conecta ao pipe, `Ping`, `List`, `Run` de um item apontando para
  `C:\Windows\System32\whoami.exe` com hash real, e um item com hash errado → `VerificationFailed`.

### Status
- [x] Código implementado (2026-10-16): Service (Program, AuditLog, PolicyProvider, PipeServer, LaunchService,
      RateLimiter, Elevation/SessionLauncher, Elevation/TokenFactory movido, Native/ServiceNative) + IntegrationTests.
      Build Release 0 warnings; 64 testes unitários verdes.
- [ ] Verificação manual abaixo **adiada para o final** (exige admin). Script `tools/verify-phase5.ps1` (rodar como admin): instala em
      `C:\Program Files\RunAsLauncher`, roda `dotnet test --filter Category=RequiresAdmin` e o teste de `icacls`.
- Desvios registrados: `FirstPipeInstance` só na 1ª instância (a próxima é criada antes de entregar a atual — sempre
  há uma instância nossa viva); ACE Deny para NETWORK no pipe; Standard/Restricted conferem que o token da sessão é do
  chamador; pasta da política criada com DACL protegida se ausente + rechecagem de ACL a cada 60 s; SID da máquina via
  `LookupAccountNameW`; `ProtectedLocationChecker.IsRiskyWritable` passou a `internal`.

### Verification
- [ ] Instalação manual (admin): `sc create RunAsLauncher binPath= "<publish>\RunAsLauncher.Service.exe" start= demand`,
      `RunAsLauncher.Service.exe --register-eventsource`, `sc start RunAsLauncher` → evento 1200.
- [ ] Política com `cmd.exe` (hash real, `AllowedSids` = SID do usuário teste) → a partir do usuário padrão, cliente de teste
      (`IntegrationTests`) dispara `Run` → `cmd` aparece elevado (`whoami /groups` = High) e evento 1000 registrado.
- [ ] Hash errado → 1001/`VerificationFailed`; alvo em `%TEMP%` → `UnprotectedLocation`; usuário fora de `AllowedSids` → `Denied`.
- [ ] `icacls` dando Modify a Users em `ProgramData\RunAsLauncher` → serviço rejeita a política (1101) e nega tudo.
- [ ] Item com `Children = Deny`: dentro do app lançado, tentar abrir outro processo falha.

---

## Phase 6: Launcher do usuário (WinForms, `asInvoker`)

### `src/RunAsLauncher/RunAsLauncher.csproj`, `app.manifest`, `Resources/app.ico`
- Action: [NEW]
- Instruction: WinForms, `asInvoker`, PerMonitorV2, single-file no publish. Ícone novo (próprio).

### `src/RunAsLauncher/Strings.cs`
- Action: [NEW]
- Instruction: padrão Fragmo `T(pt, en)`; textos de todos os `ResultCode` em linguagem simples
  (ex.: `UnprotectedLocation` → "O programa está numa pasta que qualquer usuário pode alterar. Peça ao administrador para movê-lo.").

### `src/RunAsLauncher/BrokerClient.cs`
- Action: [NEW]
- Instruction: `NamedPipeClientStream(".", PipeNames.Main, InOut, Asynchronous, TokenImpersonationLevel.Identification)`,
  timeout de conexão 3 s; serviço ausente → mensagem "Serviço RunAs Launcher não está em execução".
  Métodos `ListAsync()`, `RunAsync(id)`.

### `src/RunAsLauncher/Program.cs`
- Action: [NEW]
- Instruction: sem argumentos → `MainForm`. `run <id>` → chama `RunAsync` sem abrir janela; erro → `MessageBox`.
  `--help` → uso.

### `src/RunAsLauncher/MainForm.cs`
- Action: [NEW]
- Instruction: `ListView` (LargeIcon/Details) com ícone extraído do alvo (`Icon.ExtractAssociatedIcon`, ou `IconPath`),
  caixa de busca filtrando por título, duplo clique/Enter = executar, menu de contexto:
  Executar · Criar atalho na área de trabalho · Copiar comando · Abrir local do arquivo. Botão Atualizar.
  Indicador do modo (escudo para `Elevated`).

### `src/RunAsLauncher/ShortcutWriter.cs`
- Action: [NEW]
- Instruction: cria `.lnk` em `Environment.SpecialFolder.DesktopDirectory` via COM `WScript.Shell`
  (`Type.GetTypeFromProgID` + `dynamic`): `TargetPath` = caminho do `RunAsLauncher.exe`, `Arguments` = `run <id>`,
  `IconLocation` = alvo,0. Nome do arquivo saneado a partir do título.
  `CommandLine(id)` (para "Copiar comando") = `"<caminho do exe>" run <id>` — função pura testável.

### `tests/RunAsLauncher.Tests/ShortcutWriterTests.cs`, `StringsTests.cs`
- Action: [NEW]
- Instruction: `CommandLine` com caminho com espaços; saneamento de nome (`\ / : * ? " < > |`); todo `ResultCode` tem texto PT e EN.
  (Adicionar referência do projeto de testes ao `RunAsLauncher`.)

### Status
- [x] Código implementado (2026-10-16): csproj/app.manifest/`Resources/app.ico` (ícone próprio: quadrado azul, seta
      "play" branca, escudo dourado), Strings, BrokerClient, Program, MainForm, ShortcutWriter + testes.
      Build Release 0 warnings; 93 testes unitários verdes.
- Desvios registrados: versão do protocolo movida para `Ipc.Protocol.Version` no Core (`PipeServer.ProtocolVersion`
  passa a apontar para ela) para o launcher não duplicar o literal; Core ganhou `InternalsVisibleTo RunAsLauncher`
  (reuso de `PolicyValidator.SlugRegex`: o id é validado antes de virar argumento de atalho/`run`);
  `CommandLine(exePath, id)` é a sobrecarga pura testada (`CommandLine(id)` usa `Environment.ProcessPath`);
  botão Ícones/Detalhes alterna a visão; F5 = atualizar, Ctrl+F = busca.

### Verification
- [ ] Como usuário padrão: lista mostra só os itens permitidos; executar abre elevado. *(adiado: exige serviço instalado — junto com a verificação da fase 5)*
- [ ] Atalho criado na área de trabalho funciona; "Copiar comando" colado no Executar (Win+R) funciona. *(adiado: idem)*
- [x] Serviço parado → mensagem clara, sem crash (verificado 2026-10-16: janela abre, lista vazia, aviso na barra de status).

---

## Phase 7: Console de administração (WinForms, `requireAdministrator`)

### `src/RunAsLauncher.Admin/RunAsLauncher.Admin.csproj`, `app.manifest`
- Action: [NEW]
- Instruction: WinForms, `requestedExecutionLevel level="requireAdministrator"`, `AssemblyName=RunAsLauncher.Admin`.

### `src/RunAsLauncher.Admin/Strings.cs`
- Action: [NEW]
- Instruction: mesmo padrão; mover a infraestrutura `T()`/`UiLanguage` comum para `src/RunAsLauncher.Core/Localization/Text.cs`
  e fazer os dois `Strings.cs` usarem-na ([MOD] `src/RunAsLauncher/Strings.cs`).

### `src/RunAsLauncher.Admin/AdminForm.cs`
- Action: [NEW]
- Instruction: lista de itens (título, caminho, modo, verificação, status do hash atual ✔/✖), botões
  Adicionar · Editar · Duplicar · Remover · Subir/Descer · Importar · Exportar · Atualizar hash de todos · Salvar.
  Drag & drop de `.exe`/`.lnk` (resolver `.lnk` via `WScript.Shell`) cria item novo pré-preenchido.
  Salvar → `PolicyStore.Save` (valida; erros mostrados por item). Aba "Auditoria".

### `src/RunAsLauncher.Admin/ItemEditorForm.cs`
- Action: [NEW]
- Instruction: campos do `PolicyItem`; botão "Calcular hash" (SHA-256 do arquivo atual); "Ler assinatura"
  (preenche `PublisherRule` com subject/thumbprint); seletor de usuários/grupos (`SID` resolvido a partir de nome via
  `NTAccount.Translate`, e mostrado como nome); aviso de **alto risco** quando o alvo é
  `cmd.exe|powershell.exe|pwsh.exe|regedit.exe|mmc.exe|explorer.exe|taskmgr.exe` ou o modo é `Elevated` com `Children = Allow`;
  aviso imediato se `ProtectedLocationChecker` reprovar o caminho.

### `src/RunAsLauncher.Admin/AuditView.cs`
- Action: [NEW]
- Instruction: lê os últimos 500 eventos da fonte `RunAsLauncher` (`EventLogReader` com XPath
  `*[System[Provider[@Name='RunAsLauncher']]]`), grid com data, usuário, item, resultado; exportar CSV.

### `src/RunAsLauncher.Core/Policy/HighRiskTargets.cs` + `tests/RunAsLauncher.Tests/HighRiskTargetsTests.cs`
- Action: [NEW]
- Instruction: lista e função pura `bool IsHighRisk(PolicyItem)` usada pelo editor; testes por nome de arquivo (case-insensitive).

### Status
- [x] Código implementado (2026-10-16): csproj/app.manifest (`requireAdministrator`, conferido no EXE), Strings,
      AdminForm, ItemEditorForm, AuditView, `Core/Localization/Text.cs` (`Text.T` + `UiLanguage`, usado pelos dois
      `Strings.cs`), `Core/Policy/HighRiskTargets.cs` + testes. Build Release 0 warnings; 113 testes unitários verdes.
- Desvios registrados: helper extra `src/RunAsLauncher.Admin/TargetFiles.cs` (hash, status ✔/✖ do arquivo atual,
  leitura de `.lnk`, item pré-preenchido, slug/id único, nome ↔ SID), compartilhado entre lista e editor;
  sem `PackageReference System.Diagnostics.EventLog` (já vem no framework WindowsDesktop — NU1510);
  ícone reaproveitado do launcher; `Strings` do console no namespace `RunAsLauncher.Admin`.
  Item novo (Adicionar/drag & drop) nasce com Modo Administrador, filhos **Negar**, pasta protegida exigida e
  nenhuma conta (aviso "ninguém poderá usar"). "Atualizar hash de todos" só recalcula itens que já usam hash
  (e o tamanho só se já era conferido). A auditoria lê a mensagem de `Properties[0]` (não depende da DLL de mensagens).
  Atalhos: Ctrl+S salva, Insert adiciona, Delete remove, duplo clique edita.

### Verification
- [ ] Abrir o console pede UAC; usuário padrão não consegue salvar sem credenciais de admin. *(adiado: exige serviço instalado/UAC — junto com a verificação manual das fases 5 e 6)*
- [ ] Criar item por drag & drop, calcular hash, salvar → serviço recarrega (evento 1100) e o Launcher mostra o item sem reiniciar. *(adiado: exige serviço instalado/UAC — junto com a verificação manual das fases 5 e 6)*
- [ ] Import/Export round-trip; aba Auditoria lista os lançamentos da Fase 5/6. *(adiado: exige serviço instalado/UAC — junto com a verificação manual das fases 5 e 6)*

---

## Phase 8: Instalador, publish e certificado autoassinado

### `installer/RunAsLauncher.iss`
- Action: [NEW]
- Instruction: Inno Setup 6, `PrivilegesRequired=admin`, `ArchitecturesInstallIn64BitMode=x64compatible`,
  `DefaultDirName={autopf}\RunAs Launcher`, AppId novo (GUID gerado). Arquivos: os 3 EXEs publicados.
  `[Dirs]` `{commonappdata}\RunAsLauncher`. `[Run]` (ordem):
  `icacls "{commonappdata}\RunAsLauncher" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX`;
  `RunAsLauncher.Service.exe --register-eventsource`;
  `sc create RunAsLauncher binPath= "\"{app}\RunAsLauncher.Service.exe\"" start= auto obj= LocalSystem DisplayName= "RunAs Launcher"`;
  `sc description`, `sc failure RunAsLauncher reset= 86400 actions= restart/5000`, `sc start RunAsLauncher`.
  Atalhos no menu Iniciar: "RunAs Launcher" e "RunAs Launcher — Administração".
  `[UninstallRun]`: `sc stop`, `sc delete`, `--unregister-eventsource`. Perguntar no uninstall se apaga a política.
  Upgrade: parar o serviço antes de copiar arquivos (`PrepareToInstall`).
  `policy.json` nunca é sobrescrito no upgrade.

### `publish.ps1`
- Action: [NEW]
- Instruction: copiar a estrutura do Fragmo: `dotnet test --filter "Category!=RequiresAdmin"` → publish dos 3 projetos
  em `artifacts/publish` → assinatura opcional → ISCC `/DAppVersion=<Version do Directory.Build.props>`.
  Assinatura: se existir `$env:RAL_SIGN_THUMBPRINT`, `Set-AuthenticodeSignature` em cada EXE e no instalador
  (`-TimestampServer` omitido em uso privado offline).

### `tools/New-SigningCert.ps1`
- Action: [NEW]
- Instruction: `New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=RunAs Launcher (privado)" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)`;
  exporta `.cer` público para `artifacts/RunAsLauncher-signing.cer` e imprime o thumbprint.
  Documentar: nas máquinas-alvo importar o `.cer` em `LocalMachine\Root` e `LocalMachine\TrustedPublisher`
  (`Import-Certificate`, admin). A chave privada **nunca** sai da máquina de build.

### Status
- [x] Código implementado (2026-10-16): `installer/RunAsLauncher.iss`, `publish.ps1`, `tools/New-SigningCert.ps1`.
      `./publish.ps1` (sem assinatura): 113 testes verdes → 3 EXEs single-file em `artifacts/publish` → instalador
      `artifacts/RunAsLauncher-Setup.exe` (~134 MB; os EXEs já vêm comprimidos, o LZMA quase não reduz).
- Desvios registrados: parada do serviço com `net stop` (no `PrepareToInstall` e no `[UninstallRun]`) em vez de
  `sc stop`, porque `sc stop` não espera e o EXE ficaria preso na cópia/remoção; `sc create` só quando o serviço
  não existe (`Check: not ServiceExists`), num upgrade roda `sc config` com o mesmo `binPath`; pasta da política
  com `uninsneveruninstall` + pergunta no `usPostUninstall` (silencioso = mantém); instalador em pt-BR/en;
  `CloseApplications=yes` (Restart Manager fecha launcher/console abertos). Assinatura do `unins000.exe` não feita
  (exigiria `SignTool` do Inno). Script de assinatura não exercitado (cria certificado no repositório do usuário).

### Verification
- [x] `./publish.ps1` gera `artifacts/RunAsLauncher-Setup.exe`.
- [ ] Instalar numa VM limpa (Win11, conta admin) → serviço em execução, `icacls` da pasta conferido, eventos 1200. *(adiado: exige VM/admin — junto com a verificação manual das fases 5–7)*
- [ ] Logar como usuário padrão → fluxo completo da Fase 6. *(adiado: exige VM/admin — junto com a verificação manual das fases 5–7)*
- [ ] Upgrade por cima preserva `policy.json`; desinstalar remove serviço e fonte de eventos. *(adiado: exige VM/admin — junto com a verificação manual das fases 5–7)*
- [ ] Com o certificado importado na VM, `Get-AuthenticodeSignature` = `Valid` nos EXEs. *(adiado: exige VM/admin — junto com a verificação manual das fases 5–7)*

---

## Phase 9: Testes de cenário e documentação

### `docs/Test-Plan.md`
- Action: [NEW]
- Instruction: roteiro manual para VM com: usuário local padrão; usuário de domínio AD (DC acessível e DC offline →
  esperar erro claro); conta Entra ID → `UnsupportedAccount`; troca do EXE após aprovar (hash) → bloqueado;
  junction para pasta gravável → bloqueado; múltiplas sessões RDP (processo abre na sessão certa);
  `Children = Deny` e `KillTreeOnExit`; serviço parado; política corrompida.

### `README.md`
- Action: [NEW]
- Instruction: o que é, requisitos (Win10 1809+/Win11, conta local ou AD, sem Entra ID), instalação, como aprovar um app,
  modelo de segurança e **limites** (app elevado com diálogo de arquivo = admin completo; aprovar só o necessário),
  onde ver a auditoria. Sem menção ao programa que inspirou o projeto.

### `LICENSE`
- Action: [NEW]
- Instruction: "All rights reserved" (licença proprietária).

### Status
- [x] Código/documentação (2026-10-16): `docs/Test-Plan.md`, `README.md`, `LICENSE`.
- Desvios registrados: o Test-Plan também cobre as verificações manuais adiadas das fases 5–8 (instalação, ACL,
  UAC, auditoria, atalhos, upgrade/desinstalação, assinatura), para uma única rodada na VM. Rótulos da UI citados
  como aparecem no console (ex.: "Encerrar todos ao fechar" = `KillTreeOnExit`).
- Pontos a observar na VM: DC offline (2.2b) resulta em `LaunchFailed` genérico no launcher — o detalhe (NTSTATUS)
  fica só no evento 1002; se for considerado pouco claro, abrir ajuste. Em 2.3, conta Entra fora de `AllowedSids`
  recebe `Denied` (a autorização é checada antes do tipo de conta).
- Extra: `tools/run-test-plan.ps1` automatiza na VM os casos com contas locais (1.1, 1.2, 1.4, 2.1, 2.4, 3.1–3.5, 4.3,
  5.1–5.3, 6.1–6.5, 7.1–7.3) e grava `C:\RALTest\results.md`; AD, Entra, RDP e UI ficam MANUAL. Cliente do pipe embutido
  validado contra um servidor falso (enquadramento) e sem serviço (`ServiceUnavailable`).

### Verification
- [ ] Todos os itens do `docs/Test-Plan.md` executados na VM e resultados anotados no próprio arquivo. *(decisão do usuário em 2026-10-16: sem VM — a validação será feita em uso real; o Test-Plan e `tools/run-test-plan.ps1` ficam como referência. As verificações manuais das fases 5–8 seguem o mesmo destino.)*
- [x] `dotnet test` verde (113); `./publish.ps1` limpo (gera `artifacts/RunAsLauncher-Setup.exe`); README revisado contra o código.
