# Verificação da Fase 5 (RunAs Launcher). Rodar num PowerShell de ADMINISTRADOR.
$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot 'verify-phase5.log'
Start-Transcript -Path $log -Force | Out-Null

$repo = 'C:\Users\Usuario\runas-launcher'
$dest = 'C:\Program Files\RunAsLauncher'
$exe  = Join-Path $dest 'RunAsLauncher.Service.exe'
$policyDir = 'C:\ProgramData\RunAsLauncher'

# Mudança de ACL só na pasta não gera evento no watcher (a rechecagem é a cada 60 s); um arquivo temporário força o reload.
function Poke { $t = Join-Path $policyDir 'poke.tmp'; Set-Content $t 'x'; Remove-Item $t -Force }

function Show-Events($n = 8) {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RunAsLauncher' } -MaxEvents $n -ErrorAction SilentlyContinue |
        Format-Table TimeCreated, Id, LevelDisplayName, @{ n = 'Msg'; e = { ($_.Message -split "`r?`n")[0..1] -join ' | ' } } -AutoSize -Wrap
}

Write-Host "== 1. Instalação" -ForegroundColor Cyan
if (Get-Service RunAsLauncher -ErrorAction SilentlyContinue) { Stop-Service RunAsLauncher -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$repo\artifacts\service\RunAsLauncher.Service.exe" $exe -Force
& $exe --register-eventsource; "register-eventsource exit=$LASTEXITCODE"
if (-not (Get-Service RunAsLauncher -ErrorAction SilentlyContinue)) {
    sc.exe create RunAsLauncher binPath= "`"$exe`"" start= demand
}
sc.exe start RunAsLauncher
Start-Sleep -Seconds 4
sc.exe query RunAsLauncher
icacls $policyDir
Show-Events

Write-Host "== 2. Testes de integração (RequiresAdmin)" -ForegroundColor Cyan
dotnet test "$repo\tests\RunAsLauncher.IntegrationTests" -c Release --filter "Category=RequiresAdmin" --logger "console;verbosity=normal"
Show-Events 14

Write-Host "== 3. ACL fraca na pasta da política -> 1101" -ForegroundColor Cyan
icacls $policyDir /grant "*S-1-5-32-545:(OI)(CI)M" | Out-Null
Poke
Start-Sleep -Seconds 3
Show-Events 3
# Restaura: remove as ACEs de Users e devolve só leitura.
icacls $policyDir /remove:g "*S-1-5-32-545" | Out-Null
icacls $policyDir /grant "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
Poke
Start-Sleep -Seconds 3
Write-Host "-- após restaurar (esperado 1100):"
Show-Events 2

Stop-Transcript | Out-Null
Write-Host "Log: $log" -ForegroundColor Green
