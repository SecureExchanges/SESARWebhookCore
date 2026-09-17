<#
.SYNOPSIS
  Desinstalle le service SESAR WebHook.

.DESCRIPTION
  Arrete et desinscrit le service, puis supprime le dossier d'application, y compris le
  fichier connectors.secrets.json qui s'y trouve. Les courriels produits sont conserves.

.EXAMPLE
  .\Uninstall-SESARWebHook.ps1
  .\Uninstall-SESARWebHook.ps1 -RemoveData -RemoveCertificate
#>
[CmdletBinding()]
param(
  [string] $InstallPath = 'C:\Program Files\SESARWebHook',
  [string] $DataPath = 'C:\SESARWebHook',

  # Dossier des courriels produits, s'il a ete personnalise a l'installation.
  [string] $OutputPath = (Join-Path $DataPath 'Output'),

  # Supprime aussi les courriels produits. Irreversible.
  [switch] $RemoveData,

  # Supprime le certificat auto-signe cree par l'installation.
  [switch] $RemoveCertificate
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'SESARWebHook'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Ce script doit etre execute dans une console PowerShell ouverte en administrateur.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
  if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds 30))
  }
  & sc.exe delete $ServiceName | Out-Null
  Write-Host "Service $ServiceName arrete et desinscrit." -ForegroundColor Green
} else {
  Write-Host "Aucun service $ServiceName installe."
}

if (Test-Path $InstallPath) {
  $hadSecrets = Test-Path (Join-Path $InstallPath 'connectors.secrets.json')
  Remove-Item -Path $InstallPath -Recurse -Force
  Write-Host "Dossier d application supprime : $InstallPath" -ForegroundColor Green
  if ($hadSecrets) {
    # Le fichier vit a la racine de l'application. Chiffre par DPAPI, il est de toute facon
    # inutilisable ailleurs : le retirer avec l'application est le comportement souhaitable.
    Write-Host '  connectors.secrets.json a ete supprime avec l application.' -ForegroundColor Green
  }
}

if ($RemoveCertificate) {
  foreach ($storeName in @('My', 'Root')) {
    Get-ChildItem -Path "Cert:\LocalMachine\$storeName" |
      Where-Object { $_.Subject -eq 'CN=SESARWebHook' } |
      ForEach-Object {
        Remove-Item -Path $_.PSPath -Force
        Write-Host "Certificat retire de LocalMachine\$storeName" -ForegroundColor Green
      }
  }
}

if ($RemoveData) {
  if (Test-Path $OutputPath) {
    Write-Warning "Suppression definitive des courriels produits : $OutputPath"
    Remove-Item -Path $OutputPath -Recurse -Force
    Write-Host 'Donnees supprimees.' -ForegroundColor Green
  }
} else {
  Write-Host ''
  Write-Host "Courriels produits conserves dans $OutputPath."
  Write-Host 'Utiliser -RemoveData pour les supprimer.'
}
