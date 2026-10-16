# Cria o certificado autoassinado de assinatura de código do RunAs Launcher (uso privado).
#
# Na MÁQUINA DE BUILD (uma vez):
#   ./tools/New-SigningCert.ps1
#   $env:RAL_SIGN_THUMBPRINT = '<thumbprint impresso>'   # o publish.ps1 passa a assinar EXEs e instalador
#
# A chave privada fica só em Cert:\CurrentUser\My desta máquina e NUNCA deve ser exportada nem copiada.
# Só o .cer público (artifacts/RunAsLauncher-signing.cer) vai para as máquinas-alvo.
#
# Em cada MÁQUINA-ALVO (PowerShell de administrador), antes de instalar:
#   Import-Certificate -FilePath .\RunAsLauncher-signing.cer -CertStoreLocation Cert:\LocalMachine\Root
#   Import-Certificate -FilePath .\RunAsLauncher-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
# Conferir depois de instalar:
#   Get-AuthenticodeSignature 'C:\Program Files\RunAs Launcher\*.exe' | Format-Table Path, Status
$ErrorActionPreference = 'Stop'

$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=RunAs Launcher (privado)' `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears(5)

$artifacts = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$cerPath = Join-Path $artifacts 'RunAsLauncher-signing.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

"Certificado público: $cerPath"
"Thumbprint:          $($cert.Thumbprint)"
''
"Para assinar no publish:  `$env:RAL_SIGN_THUMBPRINT = '$($cert.Thumbprint)'"
