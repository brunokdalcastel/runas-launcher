# PRD: RunAs Launcher — elevação controlada de apps aprovados para usuários padrão

> Fase: Research (SDD). Próxima etapa: `/spec` → `docs/Spec.md`.
> Nome do produto provisório: **RunAs Launcher** (ver Q1).

## Context & Goal

Em máquinas Windows onde o usuário trabalha como **usuário padrão**, alguns programas específicos (ex.: regedit,
ferramentas de fabricante, instaladores internos) exigem administrador. Ferramentas comuns de "RunAs" resolvem isso
**guardando a senha de uma conta admin**, criptografada no disco, e chamando `CreateProcessWithLogonW`.

Esse modelo tem fraquezas conhecidas:
a senha é recuperável no lado do cliente, as regras ficam numa pasta gravável pelo usuário, um app elevado com
diálogo "Abrir arquivo" vira um `cmd` admin, e não há auditoria nem gestão central.

**Objetivo:** um software próprio (código e marca nossos) que permita a um usuário padrão
executar **apenas apps aprovados por um administrador**, elevados, **sem que exista senha de admin armazenada** e
com auditoria. Modelo de referência: Microsoft Intune EPM ("elevation rules", "elevate as current user",
"child process behavior").

### Requisitos funcionais (paridade + melhorias)

| # | Requisito | Origem |
|---|---|---|
| F1 | Lista de apps aprovados com título, ícone, caminho, argumentos fixos, diretório de trabalho, modo de janela (normal/oculta/min/max) | básico de qualquer launcher |
| F2 | Modos por item: **Elevado**, **Padrão** (direitos do próprio usuário), **Restrito** (token reduzido) | paridade |
| F3 | Validação do alvo antes de cada execução: **SHA-256** e/ou **assinatura Authenticode (publisher)**, tamanho | paridade, reforçado |
| F4 | Exigir que o alvo esteja em pasta **não gravável** por usuários padrão (checagem de DACL) | melhoria (EPM recomenda) |
| F5 | Escopo por item: quais usuários/grupos (local/AD) podem usar | melhoria |
| ~~F6~~ | ~~Confirmação opcional por item~~ — fora da v1 (decisão Q4: abre direto) | — |
| F7 | Política de processos-filho: **permitir**, **negar** (`PROCESS_CREATION_CHILD_PROCESS_RESTRICTED`), ou matar a árvore ao fechar (Job Object) | melhoria (mitiga escape) |
| F8 | Launcher para o usuário: lista com ícones, busca, executar, **criar atalho na área de trabalho**, **copiar comando** (`RunAsLauncher.exe run <id>`) | paridade |
| F9 | Console de administração (exige admin/UAC): CRUD de itens, drag & drop de arquivos, reordenar, "atualizar hash", import/export JSON | paridade |
| F10 | Auditoria no **Windows Event Log** (quem, o quê, hash, resultado, justificativa) + visualização no console | melhoria |
| F11 | Interface PT-BR e EN | paridade (simplificado) |
| F12 | Desinstalação limpa (serviço, pasta de política, atalhos) | paridade (`/Uninstall`) |

### Não-objetivos (v1)
Contas **Entra ID** e Microsoft Account, modo "conta isolada", gestão via nuvem/Intune, aprovação remota por suporte ("support approved"), elevação de `.ps1`/`.msi` arbitrários,
macOS/Linux, multi-idioma além de PT/EN, versão 32 bits.

## Impacted Files

O repositório `C:\Users\Usuario\runas-launcher` só contém documentação; **todos os arquivos de código são novos**.
Estrutura proposta (espelha o layout do Fragmo):

| Caminho | Responsabilidade |
|---|---|
| `docs/PRD.md` | (este arquivo) |
| `RunAsLauncher.sln`, `nuget.config`, `.gitignore`, `LICENSE`, `README.md` | Solução e metadados |
| `src/RunAsLauncher.Core/` | Biblioteca sem UI: modelo de política (`PolicyItem`, `Policy`), `PolicyStore` (JSON atômico), `PolicyValidator`, `TargetVerifier` (hash, Authenticode, DACL da pasta), protocolo IPC (DTOs + serialização) |
| `src/RunAsLauncher.Core/Native/` | P/Invoke escrito à mão (`Advapi32`, `Secur32`, `Kernel32`, `Userenv`, `Wtsapi32`, `Wintrust`) |
| `src/RunAsLauncher.Service/` | Worker Service rodando como **LocalSystem**: servidor de named pipe, autorização do chamador, criação do token, lançamento na sessão do usuário, Event Log |
| `src/RunAsLauncher.Service/Elevation/` | `TokenFactory` (S4U + `LocalGroups`), `SessionLauncher` (`CreateProcessAsUser` + `STARTUPINFOEX` + Job Object + child policy) |
| `src/RunAsLauncher/` | App do usuário (WinForms, `asInvoker`): lista, executar, atalhos, copiar comando, CLI `run <id>` |
| `src/RunAsLauncher.Admin/` | Console de administração (WinForms, `requireAdministrator`): edição da política e visualização da auditoria |
| `tests/RunAsLauncher.Tests/` | xUnit: política, validador, verificador de alvo, protocolo IPC, strings |
| `tests/RunAsLauncher.IntegrationTests/` | Testes que exigem admin/VM (token, lançamento) — fora do `dotnet test` padrão |
| `installer/RunAsLauncher.iss` | Inno Setup **admin** (instala em Program Files, registra o serviço, cria `ProgramData` com ACL) |
| `publish.ps1` | Testes → publish → instalador (mesmo fluxo do Fragmo) |

Arquivos de runtime (criados pelo instalador ou pelo serviço):
`C:\ProgramData\RunAsLauncher\policy.json` (ACL: SYSTEM + Administrators **Full**, Users **Read**),
fonte de Event Log `RunAsLauncher` em `Application`.

## Reference Patterns

Padrões do Fragmo (`C:\Users\Usuario\fragmo`) a reaproveitar, para manter o mesmo estilo:

**1. csproj** (`src/Fragmo/Fragmo.csproj`): TFM, nullable, warnings como erro, single-file só no publish.
```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<DebugType>embedded</DebugType>
<PropertyGroup Condition="'$(_IsPublishing)' == 'true'">
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
</PropertyGroup>
<ItemGroup><InternalsVisibleTo Include="Fragmo.Tests" /></ItemGroup>
```

**2. P/Invoke** (`src/Fragmo/Native/User32.cs`): `internal static partial class` + `[LibraryImport]` gerado na compilação.
```csharp
internal static partial class User32
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
}
```
→ Mesmo padrão para `Advapi32.CreateProcessAsUserW`, `Secur32.LsaLogonUser` etc., com `SafeHandle` próprios.

**3. Persistência JSON atômica e tolerante a corrupção** (`src/Fragmo/Settings/SettingsStore.cs`):
```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};
// Save: escreve em .tmp e faz File.Move(tmp, FilePath, overwrite: true)
// Load: JsonException → move para .bad.json e volta ao padrão
```
→ `PolicyStore` igual, **com uma diferença de segurança**: política corrompida = **negar tudo** (não "padrão permissivo").

**4. Textos bilíngues** (`src/Fragmo/Strings.cs`): `T(pt, en)` com idioma `System|Portuguese|English`.

**5. Testes** (`tests/Fragmo.Tests`): xUnit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, `<Using Include="Xunit" />`,
lógica pura separada da UI (ex.: `PrintScreenKeyFilter`) para ser testável.

**6. Publish** (`publish.ps1`): `dotnet test` → `dotnet publish -c Release -r win-x64` → ISCC com `/DAppVersion`.

## External Docs (Optional)

### Mecanismo de elevação sem senha (núcleo do produto)

- **`LsaLogonUser`** (ntsecapi.h) — https://learn.microsoft.com/windows/win32/api/ntsecapi/nf-ntsecapi-lsalogonuser
  - O parâmetro `LocalGroups` "adiciona grupos ao token do usuário autenticado"; exige **SeTcbPrivilege**
    (LocalSystem tem) e handle de `LsaRegisterLogonProcess`.
  - Logon **S4U** (`MSV1_0_S4U_LOGON` para contas locais, `KERB_S4U_LOGON` para domínio) gera token **sem senha**.
  - Logon `Batch`/`Interactive` → token primário; `Network` → token de impersonação.
- Fluxo proposto no serviço:
  1. `WTSQueryUserToken(sessionId)` / identidade do cliente do pipe → SID e nome do usuário.
  2. `LsaLogonUser(S4U, LogonType=Batch, LocalGroups=[BUILTIN\Administrators])` → token do **próprio usuário**
     com o grupo Administrators (= EPM "elevate as current user": perfil, HKCU e ambiente do usuário preservados).
  3. Se o token voltar filtrado pelo UAC, usar `TokenLinkedToken` (ver R1).
  4. `SetTokenInformation(TokenSessionId)` = sessão do usuário; `CreateEnvironmentBlock`; `LoadUserProfile` se preciso.
  5. `CreateProcessAsUserW` com `STARTUPINFOEX`, `lpDesktop = "winsta0\\default"`, `EXTENDED_STARTUPINFO_PRESENT`.
- **Alternativa / modo opcional "conta isolada"**: EPM recomenda, quando há compatibilidade, elevar com uma
  **conta virtual** separada do perfil do usuário. Equivalente nosso: conta local dedicada criada pelo instalador,
  senha aleatória guardada só em LSA Secrets (`LsaStorePrivateData`, legível apenas por SYSTEM).
- Intune EPM (conceitos, tipos de elevação, child process, regras por hash/cert/caminho):
  https://learn.microsoft.com/intune/epm/deployment-planning — https://learn.microsoft.com/intune/epm/create-elevation-rules
  - "File hash rules são as mais fortes"; "certificado + nome de arquivo sozinho não é recomendado";
    "use caminho que usuários padrão não possam modificar".

### Controle de processos-filho
- **`UpdateProcThreadAttribute`** — https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
  - `PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY` + `PROCESS_CREATION_CHILD_PROCESS_RESTRICTED (0x01)`: o processo
    não pode criar filhos (Win10+). **Ressalva da doc:** só é totalmente efetivo em sandbox; um processo com
    acesso a handle de outro processo (`PROCESS_CREATE_PROCESS`/`VM_WRITE`) pode contornar. Para um processo
    elevado isso é **mitigação, não fronteira** (ver R3).
  - `PROC_THREAD_ATTRIBUTE_JOB_LIST`: atribui o processo a um Job Object de forma atômica (kill-on-close, limites).

### IPC e serviço (.NET)
- `NamedPipeServerStreamAcl.Create(..., PipeSecurity)` (System.IO.Pipes) para ACL do pipe;
  `NamedPipeServerStream.GetImpersonationUserName()` / `RunAsClient` para identificar o chamador.
  O serviço **nunca confia em dados do cliente** além do `itemId` (e justificativa); tudo vem da política.
- `Microsoft.Extensions.Hosting.WindowsServices` → `builder.Services.AddWindowsService(...)`.
- `System.Diagnostics.EventLog` (pacote `System.Diagnostics.EventLog`) para auditoria.
- Authenticode: `WinVerifyTrust` (wintrust.dll) para validade + `X509Certificate.CreateFromSignedFile` para o publisher.

## Proposed Tech Stack

| Camada | Escolha | Motivo |
|---|---|---|
| Linguagem/runtime | C# / **.NET 10** (SDK 10.0.402 instalado), `net10.0-windows10.0.19041.0` | Mesmo stack do Fragmo |
| Serviço | Worker Service + `Microsoft.Extensions.Hosting.WindowsServices`, conta **LocalSystem** | Precisa de SeTcbPrivilege |
| UI (Launcher e Admin) | **WinForms** | Mesmo do Fragmo; leve; sem trimming |
| Interop | `[LibraryImport]` escrito à mão + `SafeHandle` | Padrão do Fragmo; sem dependência extra |
| Serialização | `System.Text.Json` (camelCase, enums como string) | Padrão do Fragmo |
| IPC | Named pipe `\\.\pipe\RunAsLauncher` com ACL (Users: ReadWrite, sem `CreateNewInstance`) | Local, autenticado pelo Windows |
| Auditoria | Windows Event Log (`Application`, fonte `RunAsLauncher`) | Integra com SIEM/Event Viewer |
| Testes | xUnit 2.9.3 + Microsoft.NET.Test.Sdk 17.14.1 | Padrão do Fragmo |
| Instalador | **Inno Setup 6** (já instalado), `PrivilegesRequired=admin`, `sc.exe create`/`[Run]` | Reaproveita `publish.ps1`; serviço exige instalação admin |
| Assinatura de código | Opcional na v1 (ver Q5) | — |

Versões de pacotes NuGet (`Microsoft.Extensions.Hosting.WindowsServices`, `System.Diagnostics.EventLog`) devem ser
as 10.0.x estáveis no momento do `/implement`.

## Questions/Risks

### Decisões do usuário (2026-10-16)
- **Q1:** nome **RunAs Launcher** mantido; repositório **privado** (tornado **público** em 2026-10-16, licença proprietária mantida).
- **Q3:** suportar **PC local e domínio AD**. **Entra ID fica fora** (revisado a pedido do usuário).
  - conta **local** → S4U via MSV1_0 (próprio usuário);
  - conta de **domínio AD** → S4U via Kerberos (`KERB_S4U_LOGON`; o PC precisa ver um DC);
  - conta **Entra ID** / Microsoft Account → o serviço recusa com mensagem clara e registra no Event Log.
- **Q2:** identidade elevada = **próprio usuário** (S4U + Administrators). O modo "conta isolada" sai da v1
  (era necessário só para Entra ID); fica como ideia para v2.
- **Q4:** **abre direto**, sem prompt de confirmação, justificativa ou senha; tudo vai para o Event Log.
  F6 sai do escopo da v1.
- **Q5:** **sem certificado** de code signing. Mitigação para uso privado: certificado autoassinado gerado por
  nós (`New-SelfSignedCertificate -Type CodeSigningCert`), instalado em *Trusted Root* + *Trusted Publishers* das
  máquinas onde o produto roda; sem isso, o SmartScreen avisa no instalador e o AV pode sinalizar (R8).

### Perguntas originais
- **Q1 — Nome e repositório:** "RunAs Launcher" é provisório. Qual nome? Vai ser público no GitHub como o Fragmo
  (MIT)? Mesma regra do Fragmo de **não citar o programa que inspirou** em material público?
- **Q2 — Identidade elevada padrão:** "elevar como o próprio usuário" (S4U + Administrators; mais compatível) ou
  "conta isolada" (mais seguro, recomendado pelo EPM)? Proposta: padrão = próprio usuário, conta isolada como opção por item na v2.
- **Q3 — Ambiente-alvo:** só PCs locais/standalone, ou também **domínio AD** e **Entra ID joined**? S4U muda por
  tipo de conta (MSV1_0 vs Kerberos; contas Entra ID precisam de validação — R2).
- **Q4 — Confirmação:** a v1 precisa de "justificativa obrigatória" e/ou re-autenticação do usuário (Windows Hello/senha
  do próprio usuário), ou basta execução automática + auditoria?
- **Q5 — Assinatura:** o serviço roda como SYSTEM; distribuir sem assinatura gera alerta do SmartScreen/AV. Teremos certificado de code signing?

### Riscos técnicos
- **R1 — Token filtrado pelo UAC (crítico; spike na Fase 1).** Não está documentado se um token S4U com
  `LocalGroups=Administrators` sai com integridade **High** e não filtrado. Plano: spike que verifica
  `TokenElevationType`/`TokenIntegrityLevel`; se vier `Limited`, obter `TokenLinkedToken` (com TCB retorna token
  primário); último recurso: definir o nível de integridade com `SetTokenInformation(TokenIntegrityLevel)`.
- **R2 — Contas Entra ID / Microsoft Account:** S4U para essas contas pode falhar ou exigir outro pacote.
  Fallback: modo "conta isolada" (F-alternativa).
- **R3 — Escape pelo app elevado:** qualquer app elevado com diálogo de arquivo permite abrir outros programas.
  `CHILD_PROCESS_RESTRICTED` é mitigação (a doc avisa que não é fronteira fora de sandbox). Documentar na UI do
  admin: "aprove apenas apps que você aceitaria dar admin completo"; marcar regedit/cmd/powershell/mmc como "alto risco".
- **R4 — TOCTOU no alvo:** o arquivo pode ser trocado entre a verificação de hash e o `CreateProcess`.
  Mitigações: F4 (pasta não gravável — torna a troca impossível para usuário padrão); abrir o arquivo com
  `FILE_SHARE_READ` (sem write/delete) durante verificação+lançamento.
- **R5 — Serviço SYSTEM é superfície de ataque:** validar tamanho das mensagens do pipe, timeouts, sem
  desserialização polimórfica, sem caminhos vindos do cliente, rate limit por usuário, pipe criado antes de
  qualquer outro processo (evitar *pipe squatting*: `FILE_FLAG_FIRST_PIPE_INSTANCE`).
- **R6 — Integridade da política:** ACL de `ProgramData\RunAsLauncher` precisa ser aplicada pelo instalador e
  **verificada pelo serviço no startup** (se Users puder escrever → recusar a política e registrar erro).
- **R7 — Testabilidade:** elevação/token só testam com admin numa VM. Separar lógica pura (validador, matcher de
  grupos, verificador de ACL com descritores sintéticos) para testes unitários; integração em projeto à parte.
- **R8 — Antivírus/EDR:** padrão "serviço SYSTEM que cria processo admin para usuário" pode ser sinalizado.
  Assinatura + descrição clara reduzem o risco.
