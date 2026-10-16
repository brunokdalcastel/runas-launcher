; Instalador do RunAs Launcher (Inno Setup 6). Gerado pelo publish.ps1:
;   ISCC /DAppVersion=x.y.z installer\RunAsLauncher.iss
; Instala para a máquina (admin) em Program Files, cria a pasta da política com ACL restrita
; e registra o serviço RunAsLauncher (LocalSystem, início automático).
; O policy.json não faz parte do pacote: nunca é criado nem sobrescrito pelo instalador.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define ServiceName "RunAsLauncher"
#define ServiceExe "RunAsLauncher.Service.exe"

[Setup]
AppId={{E69CA4D6-E23D-440D-B544-EDA86EFDC8FB}
AppName=RunAs Launcher
AppVersion={#AppVersion}
AppVerName=RunAs Launcher {#AppVersion}
AppPublisher=Bruno Koch Dal Castel
DefaultDirName={autopf}\RunAs Launcher
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=RunAsLauncher-Setup
SetupIconFile=..\src\RunAsLauncher\Resources\app.ico
UninstallDisplayIcon={app}\RunAsLauncher.exe
UninstallDisplayName=RunAs Launcher
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; O serviço é parado no PrepareToInstall; o launcher e o console são fechados pelo Restart Manager.
CloseApplications=yes
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
brazilianportuguese.AdminShortcut=RunAs Launcher — Administração
english.AdminShortcut=RunAs Launcher — Administration
brazilianportuguese.ServiceDescription=Abre, com privilégios de administrador, os programas autorizados pela política do RunAs Launcher.
english.ServiceDescription=Opens, with administrator privileges, the programs allowed by the RunAs Launcher policy.
brazilianportuguese.StatusAcl=Protegendo a pasta da política...
english.StatusAcl=Protecting the policy folder...
brazilianportuguese.StatusService=Registrando o serviço...
english.StatusService=Registering the service...
brazilianportuguese.StopFailed=Não foi possível parar o serviço RunAs Launcher. Pare-o manualmente (services.msc) e rode o instalador de novo.
english.StopFailed=Could not stop the RunAs Launcher service. Stop it manually (services.msc) and run the installer again.
brazilianportuguese.DeletePolicy=Apagar também a política (%1)?%n%nEscolha "Não" para mantê-la e reaproveitá-la numa reinstalação.
english.DeletePolicy=Also delete the policy (%1)?%n%nChoose "No" to keep it for a future reinstall.

[Files]
Source: "..\artifacts\publish\RunAsLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\publish\RunAsLauncher.Admin.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\publish\{#ServiceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\RunAsLauncher"; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\RunAs Launcher"; Filename: "{app}\RunAsLauncher.exe"
Name: "{autoprograms}\{cm:AdminShortcut}"; Filename: "{app}\RunAsLauncher.Admin.exe"

[Run]
; 1. ACL da pasta da política: só SYSTEM e Administradores escrevem; Usuários só leem. Sem herança.
Filename: "{sys}\icacls.exe"; \
  Parameters: """{commonappdata}\RunAsLauncher"" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX"; \
  StatusMsg: "{cm:StatusAcl}"; Flags: runhidden waituntilterminated
; 2. Fonte de eventos (idempotente).
Filename: "{app}\{#ServiceExe}"; Parameters: "--register-eventsource"; \
  StatusMsg: "{cm:StatusService}"; Flags: runhidden waituntilterminated
; 3. Serviço. binPath entre aspas (caminho com espaços). Num upgrade o serviço já existe: só reconfigura o caminho.
Filename: "{sys}\sc.exe"; \
  Parameters: "create {#ServiceName} binPath= ""\""{app}\{#ServiceExe}\"""" start= auto obj= LocalSystem DisplayName= ""RunAs Launcher"""; \
  StatusMsg: "{cm:StatusService}"; Flags: runhidden waituntilterminated; Check: not ServiceExists
Filename: "{sys}\sc.exe"; \
  Parameters: "config {#ServiceName} binPath= ""\""{app}\{#ServiceExe}\"""" start= auto obj= LocalSystem"; \
  StatusMsg: "{cm:StatusService}"; Flags: runhidden waituntilterminated; Check: ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""{cm:ServiceDescription}"""; \
  StatusMsg: "{cm:StatusService}"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000"; \
  StatusMsg: "{cm:StatusService}"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; \
  StatusMsg: "{cm:StatusService}"; Flags: runhidden waituntilterminated

[UninstallRun]
; net stop espera o serviço parar de fato (sc stop só pede); assim o EXE não fica preso na remoção dos arquivos.
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{app}\{#ServiceExe}"; Parameters: "--unregister-eventsource"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterEventSource"

[Code]
const
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\{#ServiceName}';

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, ServiceKey);
end;

// Upgrade: o serviço segura o EXE aberto; precisa parar antes de copiar os arquivos.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not ServiceExists then
    Exit;
  // net stop espera a parada. Serviço já parado devolve 2 (erro genérico do net); se a parada falhou de
  // verdade, o EXE continua preso e a cópia dos arquivos mostra o diálogo de "tentar de novo" do Inno.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if (ResultCode <> 0) and (ResultCode <> 2) then
    Result := CustomMessage('StopFailed');
end;

// Desinstalação: a pasta da política fica (uninsneveruninstall) salvo se o admin pedir para apagar.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  PolicyDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  PolicyDir := ExpandConstant('{commonappdata}\RunAsLauncher');
  if not DirExists(PolicyDir) then
    Exit;
  // Em modo silencioso a resposta padrão é "Não": a política é preservada.
  if SuppressibleMsgBox(FmtMessage(CustomMessage('DeletePolicy'), [PolicyDir]),
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    DelTree(PolicyDir, True, True, True);
end;
