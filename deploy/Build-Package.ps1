<#
.SYNOPSIS
  Construit le paquet d'installation du connecteur SESAR WebHook (cote Secure Exchanges).

.DESCRIPTION
  Publie l'API, y joint les scripts d'installation et produit une archive .zip a remettre
  au client, accompagnee de son empreinte SHA-256.

  Par defaut la publication est autonome (self-contained) : le poste client n'a aucun
  runtime .NET a installer. Le revers est que les correctifs de securite du runtime .NET
  ne viennent plus de Windows Update : il faut republier un paquet a chaque correctif.
  Utiliser -FrameworkDependent pour dependre du runtime ASP.NET Core 9 installe sur le poste.

.EXAMPLE
  .\deploy\Build-Package.ps1
  .\deploy\Build-Package.ps1 -FrameworkDependent -OutputDirectory C:\Livraisons
#>
[CmdletBinding()]
param(
  # Dossier ou deposer l'archive produite.
  [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),

  # Publier en dependant du runtime ASP.NET Core 9 deja installe sur le poste client.
  [switch] $FrameworkDependent,

  # Etiquette ajoutee au nom de l'archive (defaut : la date du jour).
  [string] $VersionLabel = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot 'SESARWebHook.API.NetCore\SESARWebHook.API.NetCore.csproj'
$testProject = Join-Path $repoRoot 'SESARWebHook.Tests.NetCore\SESARWebHook.Tests.NetCore.csproj'

if (-not (Test-Path $apiProject)) { throw "Projet API introuvable : $apiProject" }

Write-Host '=== 1/5 Tests unitaires ===' -ForegroundColor Cyan
# Le poste de build peut n'avoir qu'un runtime .NET plus recent que la cible : on autorise
# la montee de version uniquement pour executer les tests.
$env:DOTNET_ROLL_FORWARD = 'Major'
dotnet test $testProject -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Les tests unitaires ont echoue : aucun paquet ne sera produit.' }
Remove-Item Env:\DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue

Write-Host '=== 2/5 Publication de l API ===' -ForegroundColor Cyan
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("SESARWebHookPackage_" + [Guid]::NewGuid().ToString('N'))
$appDir = Join-Path $staging 'app'
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

$publishArgs = @(
  'publish', $apiProject,
  '-c', 'Release',
  '-r', 'win-x64',
  '-o', $appDir,
  '--nologo'
)
if ($FrameworkDependent) {
  $publishArgs += @('--self-contained', 'false')
  $runtimeNote = 'framework-dependent (runtime ASP.NET Core 9 requis sur le poste)'
} else {
  $publishArgs += @('--self-contained', 'true')
  $runtimeNote = 'autonome (aucun runtime .NET a installer)'
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'La publication a echoue.' }

Write-Host '=== 3/5 Verifications du contenu ===' -ForegroundColor Cyan

# Rien qui vienne du harnais de tests ne doit partir chez un client.
$testArtifacts = Get-ChildItem -Path $appDir -Recurse -File |
  Where-Object { $_.Name -match 'MSTest|TestPlatform|Microsoft\.Testing|Tests\.NetCore' }
if ($testArtifacts) {
  throw ("Des binaires de test sont presents dans la publication : " +
         (($testArtifacts | Select-Object -ExpandProperty Name) -join ', '))
}

# Aucun secret ne doit se retrouver dans le paquet : tout fichier connectors.secrets*.json
# autre que le gabarit est un secret (copies locales, sauvegardes, variantes par poste).
$secretFiles = Get-ChildItem -Path $appDir -Recurse -File -Filter 'connectors.secrets*.json' |
  Where-Object { $_.Name -ne 'connectors.secrets.template.json' }
if ($secretFiles) {
  throw ("Des fichiers de secrets sont presents dans la publication, ils ne doivent jamais etre distribues : " +
         (($secretFiles | Select-Object -ExpandProperty Name) -join ', '))
}

$exe = Join-Path $appDir 'SESARWebHook.API.NetCore.exe'
if (-not (Test-Path $exe)) { throw "Executable introuvable dans la publication : $exe" }

# Les endpoints de debogage acceptent un manifest arbitraire sans authentification.
$shipped = Get-Content (Join-Path $appDir 'appsettings.json') -Raw | ConvertFrom-Json
if ("$($shipped.EnableDebugEndpoints)" -ne 'false') {
  throw 'EnableDebugEndpoints doit valoir "false" dans appsettings.json avant toute livraison.'
}

Write-Host '=== 4/5 Assemblage du paquet ===' -ForegroundColor Cyan

# Dossier des handlers clients, scanne au demarrage.
$handlers = Join-Path $appDir 'Handlers'
New-Item -ItemType Directory -Path $handlers -Force | Out-Null
Set-Content -Path (Join-Path $handlers 'LISEZMOI.txt') -Encoding utf8 -Value @'
Deposer ici les DLL de handlers personnalises. Le service les detecte au demarrage.
Dossier vide par defaut.
'@

foreach ($item in @('Install-SESARWebHook.ps1', 'Uninstall-SESARWebHook.ps1', 'INSTALL.md')) {
  Copy-Item -Path (Join-Path $PSScriptRoot $item) -Destination $staging -Force
}
Copy-Item -Path (Join-Path $repoRoot 'SESARWebHook.API.NetCore\connectors.secrets.template.json') `
          -Destination $staging -Force

Write-Host '=== 5/5 Archive ===' -ForegroundColor Cyan
if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
$zipPath = Join-Path $OutputDirectory ("SESARWebHook-$VersionLabel.zip")
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item $staging -Recurse -Force

$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ''
Write-Host 'Paquet pret.' -ForegroundColor Green
Write-Host "  Archive      : $zipPath"
Write-Host "  Taille       : $sizeMb Mo"
Write-Host "  Mode runtime : $runtimeNote"
Write-Host "  SHA-256      : $hash"
Write-Host ''
Write-Host 'Transmettre l empreinte SHA-256 au client par un canal distinct de l archive,'
Write-Host 'afin qu il verifie l integrite avant d installer.'
