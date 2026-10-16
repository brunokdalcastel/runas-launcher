# Roda os testes (sem os que exigem admin) e, se passarem, publica os 3 EXEs em artifacts/publish
# e gera o instalador artifacts/RunAsLauncher-Setup.exe (se o Inno Setup 6 estiver instalado).
# Assinatura opcional: defina $env:RAL_SIGN_THUMBPRINT com o thumbprint do certificado de
# Cert:\CurrentUser\My (ver tools/New-SigningCert.ps1). Sem timestamp: uso privado, offline.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test --filter "Category!=RequiresAdmin"
if ($LASTEXITCODE -ne 0) { Write-Error 'Testes falharam; nada foi publicado. / Tests failed; nothing was published.' }

$publishDir = Join-Path $PSScriptRoot 'artifacts/publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$projects = 'src/RunAsLauncher', 'src/RunAsLauncher.Admin', 'src/RunAsLauncher.Service'
foreach ($project in $projects) {
    dotnet publish $project -c Release -r win-x64 -o $publishDir
    if ($LASTEXITCODE -ne 0) { Write-Error "Falha no dotnet publish de $project. / dotnet publish failed for $project." }
}

$exes = 'RunAsLauncher.exe', 'RunAsLauncher.Admin.exe', 'RunAsLauncher.Service.exe' |
    ForEach-Object { Get-Item (Join-Path $publishDir $_) }
$exes | ForEach-Object { '{0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }

# Certificado de assinatura (opcional).
$cert = $null
if ($env:RAL_SIGN_THUMBPRINT) {
    $cert = Get-Item "Cert:\CurrentUser\My\$($env:RAL_SIGN_THUMBPRINT)" -ErrorAction SilentlyContinue
    if (-not $cert -or -not $cert.HasPrivateKey) {
        Write-Error "Certificado $($env:RAL_SIGN_THUMBPRINT) não encontrado em Cert:\CurrentUser\My (ou sem chave privada)."
    }
}

function Sign-File([string] $path) {
    $sig = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256
    # Autoassinado sem a raiz importada nesta máquina dá UnknownError; o que importa é ter assinado.
    if (-not $sig.SignerCertificate) { Write-Error "Falha ao assinar $path : $($sig.StatusMessage)" }
    '  assinado: {0} ({1})' -f (Split-Path $path -Leaf), $sig.Status
}

if ($cert) { $exes | ForEach-Object { Sign-File $_.FullName } }

# Instalador: procura o compilador do Inno Setup 6 (winget install JRSoftware.InnoSetup).
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning 'Inno Setup 6 não encontrado; instalador não gerado. / Inno Setup 6 not found; installer skipped.'
    return
}

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
& $iscc /Q "/DAppVersion=$version" installer/RunAsLauncher.iss
if ($LASTEXITCODE -ne 0) { Write-Error 'Falha ao gerar o instalador. / Installer build failed.' }

$setup = Get-Item (Join-Path $PSScriptRoot 'artifacts/RunAsLauncher-Setup.exe')
if ($cert) { Sign-File $setup.FullName }
'{0}  ({1:N1} MB)' -f $setup.FullName, ($setup.Length / 1MB)
