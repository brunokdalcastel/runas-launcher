#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Executa os casos automatizáveis de docs/Test-Plan.md numa VM de teste (contas locais).

.DESCRIPTION
    Rodar num PowerShell de ADMINISTRADOR (labadmin) dentro da VM, com o snapshot limpo. O script:
      - cria os usuários padrão locais labuser e labuser2;
      - instala artifacts/RunAsLauncher-Setup.exe em modo silencioso;
      - grava uma política de teste e executa os casos como labuser (tarefa agendada interativa, na sessão
        dele — por isso labuser precisa estar logado via "Trocar usuário") e como labuser2 (Start-Process -Credential);
      - confere o ResultCode devolvido pelo pipe, o evento de auditoria e o efeito (whoami, processos filhos, ACL);
      - grava C:\RALTest\results.md (OK / FALHOU / MANUAL / PULADO) e C:\RALTest\run.log.
    Casos que dependem de AD, Entra ID, RDP ou de cliques na interface ficam como MANUAL.

    Os itens da política de teste diferem da tabela-base do Test-Plan (alvos com argumentos que deixam
    evidência em C:\RALTest\out), mas cobrem os mesmos casos.

    Instala um serviço SYSTEM que concede elevação sem senha: use só em VM descartável.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\run-test-plan.ps1
#>
param(
    [string]$Setup = (Join-Path $PSScriptRoot '..\artifacts\RunAsLauncher-Setup.exe'),
    [string]$WorkDir = 'C:\RALTest',
    [securestring]$LabPassword,
    [switch]$SkipUninstall,
    [switch]$AllowPhysical
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'RunAsLauncher'
$EventSource = 'RunAsLauncher'
$PolicyDir   = Join-Path $env:ProgramData 'RunAsLauncher'
$PolicyFile  = Join-Path $PolicyDir 'policy.json'
$InstallDir  = Join-Path $env:ProgramFiles 'RunAs Launcher'
$UninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E69CA4D6-E23D-440D-B544-EDA86EFDC8FB}_is1'
$TaskName    = 'RALTestClient'
$LabUser     = 'labuser'
$LabUser2    = 'labuser2'
$Out         = Join-Path $WorkDir 'out'
$Writable    = Join-Path $WorkDir 'writable'
$Client      = Join-Path $WorkDir 'client.ps1'
$Cmd         = Join-Path $env:SystemRoot 'System32\cmd.exe'
$Ping        = Join-Path $env:SystemRoot 'System32\ping.exe'
$AppTesteDir = Join-Path $env:ProgramFiles 'RALAppTeste'
$AppTesteExe = Join-Path $AppTesteDir 'app.exe'
$JTesteDir   = Join-Path $env:ProgramFiles 'RALJTeste'
$JTesteExe   = Join-Path $JTesteDir 'app.exe'
$OutraDir    = Join-Path $env:ProgramFiles 'RALOutra'

$Results = New-Object System.Collections.Generic.List[object]
$script:Recent = @()   # horários dos pedidos de labuser (limite de 20/min por SID)

# ---------------------------------------------------------------------------------------------------------------
# Cliente do pipe (roda como labuser/labuser2). Protocolo: 4 bytes LE com o tamanho + JSON UTF-8 camelCase.
# ---------------------------------------------------------------------------------------------------------------
$ClientCode = @'
param([Parameter(Mandatory = $true)][string]$Type, [string]$ItemId,
      [Parameter(Mandatory = $true)][string]$Out, [int]$Repeat = 1)

function Read-Exactly($stream, [int]$count) {
    $buffer = New-Object byte[] $count
    $offset = 0
    while ($offset -lt $count) {
        $read = $stream.Read($buffer, $offset, $count - $offset)
        if ($read -le 0) { throw [System.IO.EndOfStreamException]::new('Pipe fechado no meio da resposta.') }
        $offset += $read
    }
    , $buffer
}

$results = @()
for ($i = 0; $i -lt $Repeat; $i++) {
    $r = [ordered]@{ code = $null; message = $null; items = @(); processId = $null; error = $null }
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'RunAsLauncher',
            [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None,
            [System.Security.Principal.TokenImpersonationLevel]::Identification)
        $pipe.Connect(3000)
        $request = [ordered]@{ version = 1; type = $Type }
        if ($ItemId) { $request.itemId = $ItemId }
        $body = [System.Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Compress))
        $frame = New-Object byte[] (4 + $body.Length)
        [BitConverter]::GetBytes([int]$body.Length).CopyTo($frame, 0)
        $body.CopyTo($frame, 4)
        $pipe.Write($frame, 0, $frame.Length)
        $pipe.Flush()
        $length = [BitConverter]::ToInt32((Read-Exactly $pipe 4), 0)
        $response = [System.Text.Encoding]::UTF8.GetString((Read-Exactly $pipe $length)) | ConvertFrom-Json
        $r.code = [string]$response.code
        $r.message = $response.message
        $r.processId = $response.processId
        if ($response.items) { $r.items = @($response.items | ForEach-Object { $_.id }) }
    }
    catch [System.TimeoutException] {
        $r.code = 'ServiceUnavailable'
        $r.error = $_.Exception.Message
    }
    catch {
        $r.code = 'ClientError'
        $r.error = $_.Exception.GetType().FullName + ': ' + $_.Exception.Message
    }
    finally {
        if ($pipe) { $pipe.Dispose() }
    }
    $results += [pscustomobject]$r
}

ConvertTo-Json -InputObject @($results) -Depth 4 | Set-Content -Path "$Out.tmp" -Encoding UTF8
Move-Item -Path "$Out.tmp" -Destination $Out -Force
'@

# ---------------------------------------------------------------------------------------------------------------
# Utilitários
# ---------------------------------------------------------------------------------------------------------------
function Write-Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Add-Result([string]$Case, [string]$Status, [string]$Detail) {
    $Results.Add([pscustomobject]@{ Case = $Case; Status = $Status; Detail = $Detail })
    $color = switch ($Status) { 'OK' { 'Green' } 'FALHOU' { 'Red' } default { 'Yellow' } }
    Write-Host ("[{0}] {1} — {2}" -f $Status, $Case, $Detail) -ForegroundColor $color
}

function Check([string]$Case, [bool]$Ok, [string]$Detail) {
    Add-Result $Case $(if ($Ok) { 'OK' } else { 'FALHOU' }) $Detail
}

function Invoke-Case([string]$Case, [scriptblock]$Body) {
    try { & $Body }
    catch { Add-Result $Case 'FALHOU' ("exceção do script: " + $_.Exception.Message) }
}

function Get-EventText($record) {
    if ($record.Properties.Count -gt 0 -and $record.Properties[0].Value -is [string]) { return $record.Properties[0].Value }
    return $record.Message
}

function Wait-RalEvent([datetime]$Since, [int]$Id, [string]$Pattern, [int]$TimeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $hit = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $EventSource; StartTime = $Since } -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -eq $Id -and (-not $Pattern -or (Get-EventText $_) -match $Pattern) } |
            Select-Object -First 1
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

function ItemLine([string]$id) { '(?m)^Item: ' + [regex]::Escape($id) + '\r?$' }
function ResultLine([string]$code) { '(?m)^Resultado: ' + $code + '\r?$' }
function EventSummary($record) { if ($record) { "evento $($record.Id)" } else { 'evento esperado NÃO encontrado' } }

function Wait-ResultFile([string]$Path, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            $parsed = Get-Content -Path $Path -Raw | ConvertFrom-Json
            return , @($parsed)
        }
        Start-Sleep -Milliseconds 500
    }
    return , @([pscustomobject]@{ code = 'NoResult'; message = $null; items = @(); processId = $null;
                                   error = "cliente não respondeu em $TimeoutSec s (labuser está logado?)" })
}

function Wait-RateBudget([int]$Count) {
    while ($true) {
        $cut = (Get-Date).AddSeconds(-62)
        $script:Recent = @($script:Recent | Where-Object { $_ -gt $cut })
        if ($script:Recent.Count -eq 0 -or $script:Recent.Count + $Count -le 18) { return }
        $wait = [int][Math]::Ceiling((($script:Recent | Sort-Object | Select-Object -First 1).AddSeconds(62) - (Get-Date)).TotalSeconds)
        Write-Host "   (aguardando $wait s pelo limite de 20 pedidos/min)" -ForegroundColor DarkGray
        Start-Sleep -Seconds ([Math]::Max(1, $wait))
    }
}

function Get-ClientArgs([string]$Type, [string]$ItemId, [string]$OutFile, [int]$Repeat) {
    $a = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Client`" -Type $Type -Out `"$OutFile`" -Repeat $Repeat"
    if ($ItemId) { $a += " -ItemId $ItemId" }
    return $a
}

# Pedido feito por labuser, na sessão interativa dele (tarefa agendada "somente quando logado").
function Invoke-LabUser([string]$Type, [string]$ItemId, [string]$Name, [int]$Repeat = 1) {
    if ($Repeat -le 18) { Wait-RateBudget $Repeat }
    $outFile = Join-Path $Out "$Name.json"
    Remove-Item $outFile -ErrorAction SilentlyContinue
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -Argument (Get-ClientArgs $Type $ItemId $outFile $Repeat)
    $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$LabUser" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $TaskName
        $result = Wait-ResultFile $outFile (30 + $Repeat * 3)
    }
    finally {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    for ($i = 0; $i -lt $Repeat; $i++) { $script:Recent += Get-Date }
    return , $result
}

# Pedido feito por labuser2, a partir desta sessão (a de labadmin).
function Invoke-LabUser2([string]$Type, [string]$ItemId, [string]$Name) {
    $outFile = Join-Path $Out "$Name.json"
    Remove-Item $outFile -ErrorAction SilentlyContinue
    $cred = New-Object System.Management.Automation.PSCredential("$env:COMPUTERNAME\$LabUser2", $LabPassword)
    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList (Get-ClientArgs $Type $ItemId $outFile 1) -Credential $cred -LoadUserProfile -WorkingDirectory $WorkDir `
        -WindowStyle Hidden -Wait
    return , (Wait-ResultFile $outFile 15)
}

function Describe-Response($r) {
    $text = "code=$($r.code)"
    if ($r.processId) { $text += " pid=$($r.processId)" }
    if ($r.message) { $text += " msg='$($r.message)'" }
    if ($r.error) { $text += " erro='$($r.error)'" }
    return $text
}

function Get-PingChildren([string]$Marker) {
    @(Get-CimInstance Win32_Process -Filter "Name='ping.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($Marker) })
}

function Test-PolicyAcl {
    $acl = Get-Acl $PolicyDir
    $problems = @()
    if (-not $acl.AreAccessRulesProtected) { $problems += 'herança ativa' }
    $seen = @{}
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne 'Allow') { $problems += "ACE Deny inesperada ($($rule.IdentityReference))"; continue }
        $sid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        $rights = $rule.FileSystemRights
        $full = [System.Security.AccessControl.FileSystemRights]::FullControl
        $write = [System.Security.AccessControl.FileSystemRights]'Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
        switch ($sid) {
            'S-1-5-18'     { if (($rights -band $full) -ne $full) { $problems += "SYSTEM sem controle total ($rights)" } }
            'S-1-5-32-544' { if (($rights -band $full) -ne $full) { $problems += "Administradores sem controle total ($rights)" } }
            'S-1-5-32-545' { if (($rights -band $write) -ne 0) { $problems += "Usuários com escrita ($rights)" } }
            default        { $problems += "ACE extra: $($rule.IdentityReference) ($rights)" }
        }
        $seen[$sid] = $true
    }
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545') { if (-not $seen[$sid]) { $problems += "falta ACE de $sid" } }
    return , $problems
}

function Poke-PolicyDir {
    $t = Join-Path $PolicyDir 'poke.tmp'
    Set-Content -Path $t -Value 'x'
    Remove-Item $t -Force
}

# Remove a pasta de teste do item jteste: a junction só como link (sem seguir), a pasta real com o conteúdo.
function Remove-JTeste {
    if (-not (Test-Path $JTesteDir)) { return }
    $dir = Get-Item -LiteralPath $JTesteDir -Force
    if ($dir.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { [System.IO.Directory]::Delete($JTesteDir) }
    else { Remove-Item -LiteralPath $JTesteDir -Recurse -Force }
}

function Install-Ral {
    $log = Join-Path $WorkDir ("setup-{0:HHmmss}.log" -f (Get-Date))
    $p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -Wait -PassThru
    return $p.ExitCode
}

function Wait-ServiceRunning([int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') { return $svc }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return Get-Service $ServiceName -ErrorAction SilentlyContinue
}

function Test-UserLoggedOn([string]$Name) {
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='explorer.exe'") {
        $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwner
        if ($owner.User -eq $Name) { return $true }
    }
    return $false
}

function New-PolicyItem([string]$Id, [string]$Target, [string]$Arguments, [string]$Mode, [string]$Children, [string[]]$Sids) {
    [ordered]@{
        id = $Id; title = "Teste $Id"; targetPath = $Target; arguments = $Arguments; workingDirectory = $null
        mode = $Mode; window = 'hidden'; children = $Children
        sha256 = (Get-FileHash -Path $Target -Algorithm SHA256).Hash
        publisher = $null; expectedSize = $null; requireProtectedLocation = $true
        allowedSids = @($Sids); iconPath = $null
    }
}

function Write-Results {
    $lines = @(
        '# Resultados — tools/run-test-plan.ps1', '',
        "Data: $(Get-Date -Format 'yyyy-MM-dd HH:mm') · Máquina: $env:COMPUTERNAME · SO: $((Get-CimInstance Win32_OperatingSystem).Caption) $((Get-CimInstance Win32_OperatingSystem).BuildNumber)",
        "Setup: $Setup ($((Get-Item $Setup).LastWriteTime))", '',
        '| Caso | Status | Detalhe |', '|---|---|---|'
    )
    foreach ($r in $Results) { $lines += "| $($r.Case) | $($r.Status) | $(($r.Detail -replace '\|', '/') -replace '\r?\n', ' ') |" }
    $file = Join-Path $WorkDir 'results.md'
    Set-Content -Path $file -Value $lines -Encoding UTF8
    Write-Host "`nResultados: $file" -ForegroundColor Green
    $Results | Group-Object Status | ForEach-Object { Write-Host ("  {0}: {1}" -f $_.Name, $_.Count) }
}

# ---------------------------------------------------------------------------------------------------------------
# 0. Preparação
# ---------------------------------------------------------------------------------------------------------------
$model = (Get-CimInstance Win32_ComputerSystem).Model
if (-not $AllowPhysical -and $model -notmatch 'Virtual|VMware|KVM|QEMU|HVM|Parallels') {
    throw "Esta máquina não parece uma VM (modelo '$model'). O script instala um serviço que concede elevação sem senha; rode numa VM descartável ou use -AllowPhysical."
}
$Setup = (Resolve-Path $Setup).Path

New-Item -ItemType Directory -Force -Path $WorkDir, $Out, $Writable | Out-Null
Start-Transcript -Path (Join-Path $WorkDir 'run.log') -Force | Out-Null
Get-ChildItem $Out -File -ErrorAction SilentlyContinue | Remove-Item -Force
icacls $Out /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
icacls $Writable /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
Set-Content -Path $Client -Value $ClientCode -Encoding UTF8

if (-not $LabPassword) { $LabPassword = Read-Host -AsSecureString "Senha para $LabUser e $LabUser2 (contas novas; ignorada se já existirem)" }

Write-Step '0. Contas de teste'
foreach ($name in $LabUser, $LabUser2) {
    if (-not (Get-LocalUser -Name $name -ErrorAction SilentlyContinue)) {
        New-LocalUser -Name $name -Password $LabPassword -PasswordNeverExpires -AccountNeverExpires -Description 'RunAs Launcher test' | Out-Null
    }
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $name -ErrorAction SilentlyContinue
    $sid = (Get-LocalUser -Name $name).SID.Value
    if (Get-LocalGroupMember -SID 'S-1-5-32-544' | Where-Object { $_.SID.Value -eq $sid }) {
        throw "$name é administrador; os casos exigem usuário padrão."
    }
}
$LabSid  = (Get-LocalUser -Name $LabUser).SID.Value
$Lab2Sid = (Get-LocalUser -Name $LabUser2).SID.Value

# Alvos: cópias de cmd.exe em pastas protegidas (Program Files) e numa pasta gravável por Usuários.
New-Item -ItemType Directory -Force -Path $AppTesteDir, $OutraDir, (Join-Path $Writable 'J') | Out-Null
Remove-JTeste
New-Item -ItemType Directory -Force -Path $JTesteDir | Out-Null
foreach ($dest in $AppTesteExe, $JTesteExe, (Join-Path $OutraDir 'app.exe'), (Join-Path $Writable 'app.exe'), (Join-Path $Writable 'J\app.exe')) {
    Copy-Item -Path $Cmd -Destination $dest -Force
}

# ---------------------------------------------------------------------------------------------------------------
# 1. Instalação e serviço
# ---------------------------------------------------------------------------------------------------------------
Write-Step '1. Instalação'
Invoke-Case '1.1 Instalação' {
    $t = (Get-Date).AddSeconds(-2)
    $exit = Install-Ral
    $svc = Wait-ServiceRunning
    $started = Wait-RalEvent $t 1200 $null 30
    $ok = $exit -eq 0 -and $svc -and $svc.Status -eq 'Running' -and $svc.StartType -eq 'Automatic' -and $started
    Check '1.1 Instalação' $ok "exit=$exit; serviço=$($svc.Status)/$($svc.StartType); $(EventSummary $started)"
}

Invoke-Case '1.2 ACL da pasta da política' {
    $problems = Test-PolicyAcl
    Check '1.2 ACL da pasta da política' ($problems.Count -eq 0) $(if ($problems.Count) { $problems -join '; ' } else { 'sem herança; SYSTEM/Admins F; Usuários RX' })
}

Add-Result '1.3 UAC do console' 'MANUAL' 'abrir "RunAs Launcher — Administração" como labadmin e como labuser'

$Items = @(
    New-PolicyItem 'cmd'          $Cmd         "/c whoami /groups /fo csv > `"$Out\cmd-whoami.csv`"" 'elevated' 'allow' @($LabSid)
    New-PolicyItem 'std-deny'     $Cmd         "/c $Ping -n 1 127.0.0.1 > `"$Out\std-deny.txt`" 2>&1" 'standard' 'deny' @($LabSid)
    New-PolicyItem 'app-teste'    $AppTesteExe "/c start `"`" /min $Ping -n 61 127.0.0.1 & $Ping -n 4 127.0.0.1 >nul" 'elevated' 'killTreeOnExit' @($LabSid)
    New-PolicyItem 'allow-tree'   $Cmd         "/c start `"`" /min $Ping -n 62 127.0.0.1" 'elevated' 'allow' @($LabSid)
    New-PolicyItem 'temp-app'     (Join-Path $Writable 'app.exe') '/c exit' 'elevated' 'deny' @($LabSid)
    New-PolicyItem 'jteste'       $JTesteExe   '/c exit' 'elevated' 'deny' @($LabSid)
    New-PolicyItem 'std-mismatch' $Cmd         '/c exit' 'standard' 'deny' @($Lab2Sid)
)
$ItemCount = $Items.Count
$LabUserIds = @('cmd', 'std-deny', 'app-teste', 'allow-tree', 'temp-app', 'jteste')

Invoke-Case '1.4 Política carregada' {
    $t = (Get-Date).AddSeconds(-1)
    $json = [ordered]@{ schemaVersion = 1; items = $Items } | ConvertTo-Json -Depth 6
    Set-Content -Path "$PolicyFile.tmp" -Value $json -Encoding UTF8
    Move-Item -Path "$PolicyFile.tmp" -Destination $PolicyFile -Force
    $loaded = Wait-RalEvent $t 1100 "\($ItemCount itens\)" 15
    $rejected = Wait-RalEvent $t 1101 $null 1
    $detail = if ($rejected) { 'política REJEITADA: ' + (Get-EventText $rejected) } else { "$(EventSummary $loaded) com $ItemCount itens" }
    Check '1.4 Política carregada' ([bool]$loaded) $detail
}
Copy-Item $PolicyFile (Join-Path $WorkDir 'policy.backup.json') -Force

# ---------------------------------------------------------------------------------------------------------------
# labuser precisa de sessão interativa
# ---------------------------------------------------------------------------------------------------------------
while (-not (Test-UserLoggedOn $LabUser)) {
    Write-Host "`n>>> Faça login como $LabUser (Iniciar → Trocar usuário), volte para esta sessão (labadmin) e pressione Enter." -ForegroundColor Magenta
    [void](Read-Host)
}

# ---------------------------------------------------------------------------------------------------------------
# 2. Contas
# ---------------------------------------------------------------------------------------------------------------
Write-Step '2. Contas'
Invoke-Case '2.1 Lista do usuário local' {
    $r = (Invoke-LabUser 'list' $null '2.1-list')[0]
    $ids = @($r.items | Sort-Object)
    $expected = @($LabUserIds | Sort-Object)
    $ok = $r.code -eq 'Ok' -and (($ids -join ',') -eq ($expected -join ','))
    Check '2.1 Lista do usuário local' $ok "$(Describe-Response $r); itens=$($ids -join ',')"
}

Invoke-Case '2.1 Executar elevado (labuser)' {
    $csv = Join-Path $Out 'cmd-whoami.csv'
    Remove-Item $csv -ErrorAction SilentlyContinue
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser 'run' 'cmd' '2.1-run')[0]
    $ev = Wait-RalEvent $t 1000 (ItemLine 'cmd')
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-Path $csv) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1
    $high = $false; $adminEnabled = $false
    if (Test-Path $csv) {
        foreach ($row in (Get-Content $csv | ConvertFrom-Csv)) {
            $values = @($row.PSObject.Properties | ForEach-Object { [string]$_.Value })
            if ($values -contains 'S-1-16-12288') { $high = $true }
            if ($values -contains 'S-1-5-32-544' -and ($values -join ' ') -notmatch 'deny|nega') { $adminEnabled = $true }
        }
    }
    $evOk = $ev -and (Get-EventText $ev) -match "(?m)^Usuário: .*\($([regex]::Escape($LabSid))\)" -and (Get-EventText $ev) -match '(?m)^Modo: Elevated' -and (Get-EventText $ev) -match "(?m)^PID: $($r.processId)\r?$"
    $ok = $r.code -eq 'Ok' -and $high -and $adminEnabled -and $evOk
    Check '2.1 Executar elevado (labuser)' $ok "$(Describe-Response $r); whoami: High=$high, Administradores habilitado=$adminEnabled; $(EventSummary $ev) (usuário/modo/PID conferem=$([bool]$evOk))"
}

Add-Result '2.2a Usuário AD, DC acessível' 'MANUAL' 'requer VM em domínio'
Add-Result '2.2b Usuário AD, DC offline' 'MANUAL' 'requer VM em domínio; esperado LaunchFailed + evento 1002 com NTSTATUS'
Add-Result '2.3 Conta Entra ID' 'MANUAL' 'requer VM ingressada no Entra ID; esperado UnsupportedAccount + 1001'

Invoke-Case '2.4 Usuário fora de AllowedSids' {
    $list = (Invoke-LabUser2 'list' $null '2.4-list')[0]
    $t = (Get-Date).AddSeconds(-1)
    $run = (Invoke-LabUser2 'run' 'std-deny' '2.4-run')[0]
    $ev = Wait-RalEvent $t 1001 ("$(ItemLine 'std-deny')[\s\S]*$(ResultLine 'Denied')")
    $ids = @($list.items)
    $ok = $list.code -eq 'Ok' -and ($ids -join ',') -eq 'std-mismatch' -and $run.code -eq 'Denied' -and $ev
    Check '2.4 Usuário fora de AllowedSids' $ok "list: $(Describe-Response $list) itens=$($ids -join ','); run: $(Describe-Response $run); $(EventSummary $ev)"
}

# ---------------------------------------------------------------------------------------------------------------
# 3. Integridade do alvo (+ 5.2, que usa o mesmo item)
# ---------------------------------------------------------------------------------------------------------------
Write-Step '3. Integridade do alvo'
Invoke-Case '3.1 EXE trocado após aprovar' {
    Copy-Item -Path (Join-Path $env:SystemRoot 'System32\where.exe') -Destination $AppTesteExe -Force
    $swappedHash = (Get-FileHash $AppTesteExe -Algorithm SHA256).Hash
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser 'run' 'app-teste' '3.1')[0]
    $ev = Wait-RalEvent $t 1001 ("$(ItemLine 'app-teste')[\s\S]*SHA-256: $swappedHash[\s\S]*$(ResultLine 'VerificationFailed')")
    Check '3.1 EXE trocado após aprovar' ($r.code -eq 'VerificationFailed' -and $ev) "$(Describe-Response $r); $(EventSummary $ev) com o SHA-256 do arquivo trocado"
}

Invoke-Case '3.2 / 5.2 Restaurado + KillTreeOnExit' {
    Copy-Item -Path $Cmd -Destination $AppTesteExe -Force
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser 'run' 'app-teste' '3.2')[0]
    $ev = Wait-RalEvent $t 1000 (ItemLine 'app-teste')
    Check '3.2 Alvo restaurado executa' ($r.code -eq 'Ok' -and $ev) "$(Describe-Response $r); $(EventSummary $ev)"

    Start-Sleep -Seconds 1
    $childBefore = (Get-PingChildren '-n 61').Count
    if ($r.processId) { Wait-Process -Id $r.processId -Timeout 20 -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
    $childAfter = Get-PingChildren '-n 61'
    Check '5.2 KillTreeOnExit' ($childBefore -gt 0 -and $childAfter.Count -eq 0) "filho vivo antes de o principal sair=$childBefore; depois=$($childAfter.Count)"
    $childAfter | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

Invoke-Case '3.3 Alvo em pasta gravável' {
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser 'run' 'temp-app' '3.3')[0]
    $ev = Wait-RalEvent $t 1001 ("$(ItemLine 'temp-app')[\s\S]*$(ResultLine 'UnprotectedLocation')")
    Check '3.3 Alvo em pasta gravável' ($r.code -eq 'UnprotectedLocation' -and $ev) "$(Describe-Response $r); $(EventSummary $ev)"
}

Invoke-Case '3.4 Junction para pasta gravável' {
    $base = (Invoke-LabUser 'run' 'jteste' '3.4-base')[0]
    Remove-JTeste
    New-Item -ItemType Junction -Path $JTesteDir -Target (Join-Path $Writable 'J') | Out-Null
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser 'run' 'jteste' '3.4')[0]
    $ev = Wait-RalEvent $t 1001 (ItemLine 'jteste')
    $ok = $base.code -eq 'Ok' -and ($r.code -eq 'UnprotectedLocation' -or $r.code -eq 'VerificationFailed') -and $ev
    Check '3.4 Junction para pasta gravável' $ok "antes da junction: $(Describe-Response $base); com junction: $(Describe-Response $r); $(EventSummary $ev)"
}

Invoke-Case '3.5 Junction para pasta protegida' {
    Remove-JTeste
    New-Item -ItemType Junction -Path $JTesteDir -Target $OutraDir | Out-Null
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser 'run' 'jteste' '3.5')[0]
    $ev = Wait-RalEvent $t 1001 ("$(ItemLine 'jteste')[\s\S]*$(ResultLine 'VerificationFailed')")
    Check '3.5 Junction para pasta protegida' ($r.code -eq 'VerificationFailed' -and $ev) "$(Describe-Response $r); $(EventSummary $ev)"
    Remove-JTeste
}

Add-Result '3.6 Troca durante a verificação' 'MANUAL' 'janela de tempo curta demais para automatizar com confiabilidade'

# ---------------------------------------------------------------------------------------------------------------
# 4. Sessões
# ---------------------------------------------------------------------------------------------------------------
Write-Step '4. Sessões'
Add-Result '4.1 Sessão RDP / segunda sessão' 'MANUAL' 'requer segunda sessão interativa (RDP ou Trocar usuário) e conferência visual'
Add-Result '4.2 Sessão do console' 'MANUAL' 'conferência visual'

Invoke-Case '4.3 Chamador != usuário interativo da sessão' {
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser2 'run' 'std-mismatch' '4.3')[0]
    $ev = Wait-RalEvent $t 1001 ("$(ItemLine 'std-mismatch')[\s\S]*$(ResultLine 'Denied')")
    Check '4.3 Chamador != usuário interativo da sessão' ($r.code -eq 'Denied' -and $ev) "labuser2 na sessão de labadmin, modo Standard: $(Describe-Response $r); $(EventSummary $ev)"
}

# ---------------------------------------------------------------------------------------------------------------
# 5. Processos filhos
# ---------------------------------------------------------------------------------------------------------------
Write-Step '5. Processos filhos'
Invoke-Case '5.1 Children = Deny' {
    $file = Join-Path $Out 'std-deny.txt'
    Remove-Item $file -ErrorAction SilentlyContinue
    $t = (Get-Date).AddSeconds(-1)
    $r = (Invoke-LabUser 'run' 'std-deny' '5.1')[0]
    $ev = Wait-RalEvent $t 1000 ("$(ItemLine 'std-deny')[\s\S]*Modo: Standard")
    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Path $file) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    $content = if (Test-Path $file) { (Get-Content $file -Raw) } else { $null }
    $childRan = $content -match 'TTL='
    $ok = $r.code -eq 'Ok' -and $ev -and $null -ne $content -and -not $childRan
    $snippet = if ($content) { ($content.Trim() -replace '\s+', ' ') } else { '(arquivo não criado)' }
    if ($snippet.Length -gt 120) { $snippet = $snippet.Substring(0, 120) }
    Check '5.1 Children = Deny' $ok "$(Describe-Response $r); $(EventSummary $ev); ping filho executou=$childRan; saída: $snippet"
}

Invoke-Case '5.3 Children = Allow' {
    $r = (Invoke-LabUser 'run' 'allow-tree' '5.3')[0]
    if ($r.processId) { Wait-Process -Id $r.processId -Timeout 15 -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
    $children = Get-PingChildren '-n 62'
    Check '5.3 Children = Allow' ($r.code -eq 'Ok' -and $children.Count -gt 0) "$(Describe-Response $r); filho vivo após o principal sair=$($children.Count)"
    $children | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------------------------------------------------
# 6. Falhas de infraestrutura
# ---------------------------------------------------------------------------------------------------------------
Write-Step '6. Falhas de infraestrutura'
Invoke-Case '6.1 Serviço parado' {
    $t = (Get-Date).AddSeconds(-1)
    Stop-Service $ServiceName -Force
    $stopped = Wait-RalEvent $t 1201 $null 15
    $r = (Invoke-LabUser 'ping' $null '6.1')[0]
    $t2 = (Get-Date).AddSeconds(-1)
    Start-Service $ServiceName
    $started = Wait-RalEvent $t2 1200 $null 20
    $reloaded = Wait-RalEvent $t2 1100 "\($ItemCount itens\)" 20
    $ok = $stopped -and $r.code -eq 'ServiceUnavailable' -and $started -and $reloaded
    Check '6.1 Serviço parado' $ok "parado: $(EventSummary $stopped); ping: $(Describe-Response $r); reinício: $(EventSummary $started), política: $(EventSummary $reloaded)"
}

Invoke-Case '6.2 Política corrompida' {
    $backup = Join-Path $WorkDir 'policy.backup.json'
    $t = (Get-Date).AddSeconds(-1)
    Set-Content -Path $PolicyFile -Value '{ "schemaVersion": 1, "items": [ ' -Encoding UTF8
    $rej = Wait-RalEvent $t 1101 $null 15
    $list = (Invoke-LabUser 'list' $null '6.2-list')[0]
    $run = (Invoke-LabUser 'run' 'cmd' '6.2-run')[0]
    $t2 = (Get-Date).AddSeconds(-1)
    Copy-Item $backup $PolicyFile -Force
    $back = Wait-RalEvent $t2 1100 "\($ItemCount itens\)" 15
    $ok = $rej -and $list.code -eq 'PolicyRejected' -and @($list.items).Count -eq 0 -and $run.code -eq 'PolicyRejected' -and $back
    Check '6.2 Política corrompida' $ok "$(EventSummary $rej); list: $(Describe-Response $list); run: $(Describe-Response $run); restaurada: $(EventSummary $back)"
}

Invoke-Case '6.3 ACL insegura' {
    $t = (Get-Date).AddSeconds(-1)
    icacls $PolicyDir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
    Poke-PolicyDir
    $rej = Wait-RalEvent $t 1101 $null 75
    $run = (Invoke-LabUser 'run' 'cmd' '6.3-run')[0]
    $t2 = (Get-Date).AddSeconds(-1)
    icacls $PolicyDir /remove:g '*S-1-5-32-545' | Out-Null
    icacls $PolicyDir /grant '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    Poke-PolicyDir
    $back = Wait-RalEvent $t2 1100 "\($ItemCount itens\)" 75
    $problems = Test-PolicyAcl
    $ok = $rej -and $run.code -eq 'PolicyRejected' -and $back -and $problems.Count -eq 0
    Check '6.3 ACL insegura' $ok "Modify para Usuários: $(EventSummary $rej); run: $(Describe-Response $run); ACL restaurada: $(EventSummary $back); ACL final ok=$($problems.Count -eq 0) $($problems -join '; ')"
}

Invoke-Case '6.4 Política inexistente' {
    $hidden = "$PolicyFile.hidden"
    $t = (Get-Date).AddSeconds(-1)
    Move-Item $PolicyFile $hidden -Force
    $empty = Wait-RalEvent $t 1100 '\(0 itens\)' 15
    $list = (Invoke-LabUser 'list' $null '6.4-list')[0]
    $t2 = (Get-Date).AddSeconds(-1)
    Move-Item $hidden $PolicyFile -Force
    $back = Wait-RalEvent $t2 1100 "\($ItemCount itens\)" 15
    $ok = $empty -and $list.code -eq 'Ok' -and @($list.items).Count -eq 0 -and $back
    Check '6.4 Política inexistente' $ok "$(EventSummary $empty) com 0 itens; list: $(Describe-Response $list) itens=$(@($list.items).Count); restaurada: $(EventSummary $back)"
}

Invoke-Case '6.5 Rate limit' {
    # Janela limpa: nenhum pedido de labuser no último minuto.
    $last = $script:Recent | Sort-Object -Descending | Select-Object -First 1
    if ($last) {
        $wait = [int][Math]::Ceiling(($last.AddSeconds(62) - (Get-Date)).TotalSeconds)
        if ($wait -gt 0) { Write-Host "   (aguardando $wait s para zerar a janela do limite)" -ForegroundColor DarkGray; Start-Sleep -Seconds $wait }
    }
    $t = (Get-Date).AddSeconds(-1)
    $rs = Invoke-LabUser 'ping' $null '6.5' 25
    $okCount = @($rs | Where-Object { $_.code -eq 'Ok' }).Count
    $limited = @($rs | Where-Object { $_.code -eq 'RateLimited' }).Count
    $ev = Wait-RalEvent $t 1001 (ResultLine 'RateLimited')
    Check '6.5 Rate limit' ($okCount -eq 20 -and $limited -eq 5 -and $ev) "25 pings: Ok=$okCount, RateLimited=$limited, outros=$(25 - $okCount - $limited); $(EventSummary $ev)"
}

# ---------------------------------------------------------------------------------------------------------------
# 7. Ciclo de vida
# ---------------------------------------------------------------------------------------------------------------
Write-Step '7. Ciclo de vida'
Invoke-Case '7.1 Upgrade preserva a política' {
    $before = (Get-FileHash $PolicyFile -Algorithm SHA256).Hash
    $t = (Get-Date).AddSeconds(-1)
    $exit = Install-Ral
    $svc = Wait-ServiceRunning
    $started = Wait-RalEvent $t 1200 $null 30
    $loaded = Wait-RalEvent $t 1100 "\($ItemCount itens\)" 30
    $after = (Get-FileHash $PolicyFile -Algorithm SHA256).Hash
    $ok = $exit -eq 0 -and $svc.Status -eq 'Running' -and $started -and $loaded -and $before -eq $after
    Check '7.1 Upgrade preserva a política' $ok "exit=$exit; serviço=$($svc.Status); $(EventSummary $started); política intacta=$($before -eq $after); $(EventSummary $loaded)"
}

Invoke-Case '7.2 Assinatura' {
    $sigs = @(Get-ChildItem $InstallDir -Filter 'RunAsLauncher*.exe' | Get-AuthenticodeSignature)
    $summary = ($sigs | ForEach-Object { "$(Split-Path $_.Path -Leaf)=$($_.Status)" }) -join ', '
    if (@($sigs | Where-Object { $_.Status -ne 'NotSigned' }).Count -eq 0) {
        Add-Result '7.2 Assinatura' 'PULADO' "EXEs não assinados (publish sem RAL_SIGN_THUMBPRINT): $summary"
    }
    else {
        Check '7.2 Assinatura' ($sigs.Count -eq 3 -and @($sigs | Where-Object { $_.Status -ne 'Valid' }).Count -eq 0) $summary
    }
}

if ($SkipUninstall) {
    Add-Result '7.3 Desinstalar mantendo a política' 'PULADO' '-SkipUninstall'
}
else {
    Invoke-Case '7.3 Desinstalar mantendo a política' {
        $uninstaller = (Get-ItemProperty $UninstallKey).UninstallString.Trim('"')
        Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' | Out-Null
        # O desinstalador do Inno se copia para %TEMP% e sai; espera pelo efeito, não pelo processo.
        $deadline = (Get-Date).AddSeconds(120)
        while ((Test-Path $UninstallKey) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
        Start-Sleep -Seconds 2
        $svcGone = -not (Get-Service $ServiceName -ErrorAction SilentlyContinue)
        $sourceGone = -not [System.Diagnostics.EventLog]::SourceExists($EventSource)
        $policyKept = Test-Path $PolicyFile
        $keyGone = -not (Test-Path $UninstallKey)
        Check '7.3 Desinstalar mantendo a política' ($keyGone -and $svcGone -and $sourceGone -and $policyKept) "desinstalado=$keyGone; serviço removido=$svcGone; fonte de eventos removida=$sourceGone; policy.json mantido=$policyKept"

        # Reinstala para deixar a VM pronta para os casos manuais.
        $t = (Get-Date).AddSeconds(-1)
        $exit = Install-Ral
        $svc = Wait-ServiceRunning
        $loaded = Wait-RalEvent $t 1100 "\($ItemCount itens\)" 30
        Check '7.3b Reinstalação reaproveita a política' ($exit -eq 0 -and $svc.Status -eq 'Running' -and $loaded) "exit=$exit; serviço=$($svc.Status); $(EventSummary $loaded)"
    }
}
Add-Result '7.4 Desinstalar apagando a política' 'MANUAL' 'exige responder "Sim" no diálogo do desinstalador'

# ---------------------------------------------------------------------------------------------------------------
# 8. Auditoria e atalhos
# ---------------------------------------------------------------------------------------------------------------
foreach ($c in '8.1 Aba Auditoria / CSV', '8.2 Atalho na área de trabalho', '8.3 Copiar comando + Win+R', '8.4 Exportar/Importar política') {
    Add-Result $c 'MANUAL' 'interface gráfica'
}

# Limpeza leve: processos de teste e junction; instalação, contas e política ficam para os casos manuais.
Get-PingChildren '127.0.0.1' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Remove-JTeste

Write-Results
Stop-Transcript | Out-Null
