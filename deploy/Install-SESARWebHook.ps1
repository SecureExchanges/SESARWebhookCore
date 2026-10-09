<#
.SYNOPSIS
  Installe le connecteur SESAR WebHook comme service Windows.

.DESCRIPTION
  A executer sur le poste ou tourne SESAR, dans une console PowerShell ouverte en
  administrateur, depuis le dossier decompresse du paquet.

  Le script : copie l'application, cree les dossiers de donnees avec des droits restreints,
  prepare le certificat TLS, ecrit la configuration du poste, enregistre le service et
  verifie qu'il repond.

  Le fichier connectors.secrets.json n'est jamais fourni par Secure Exchanges : il appartient
  au client. Le deposer a cote de ce script, ou indiquer son chemin avec -SecretsFile.

  A executer depuis une console PowerShell, pas par double-clic : Windows ouvre les fichiers
  .ps1 dans le Bloc-notes au lieu de les executer.

.EXAMPLE
  # connectors.secrets.json depose a cote du script
  .\Install-SESARWebHook.ps1

.EXAMPLE
  .\Install-SESARWebHook.ps1 -SecretsFile D:\Secrets\connectors.secrets.json `
                             -ServiceAccount 'CONTOSO\svc_sesarwebhook' `
                             -SesarStorePath 'D:\SESAR\Store'

.EXAMPLE
  # appsettings.json deja configure : ses valeurs sont respectees telles quelles
  .\Install-SESARWebHook.ps1 -AppSettingsFile D:\Config\appsettings.json
#>
[CmdletBinding()]
param(
  # Dossier d'installation de l'application (remplace a chaque mise a jour).
  [string] $InstallPath = 'C:\Program Files\SESARWebHook',

  # Dossier parent par defaut des courriels produits.
  [string] $DataPath = 'C:\SESARWebHook',

  # Dossier ou sont ecrits les courriels reconstruits. Fourni ici, il est impose au service.
  # Omis, la valeur d'appsettings.json est respectee.
  [string] $OutputPath = (Join-Path $DataPath 'Output'),

  # appsettings.json deja configure, copie par-dessus celui du paquet. Ses valeurs sont
  # respectees : l'installation n'impose que le point d'ecoute et le certificat.
  [string] $AppSettingsFile,

  # connectors.secrets.json du client. Par defaut, le script prend celui depose a cote de lui.
  [string] $SecretsFile,

  # Supprime le connectors.secrets.json source apres l'avoir copie dans le dossier d'application.
  # Recommande lorsque le fichier en clair se trouve dans un dossier peu protege.
  [switch] $RemoveSourceSecrets,

  # Port HTTPS d'ecoute, en boucle locale uniquement.
  [int] $Port = 5001,

  # Empreinte d'un certificat deja present dans LocalMachine\My. Sinon un certificat
  # auto-signe limite a ce poste est cree et ajoute aux autorites de confiance locales.
  [string] $CertificateThumbprint,

  # Compte de service. Par defaut LocalSystem. Un compte dedie est preferable :
  # le script demandera alors son mot de passe dans une invite Windows.
  [string] $ServiceAccount,

  # Dossier de stockage de SESAR, verifie en lecture pour le compte de service.
  [string] $SesarStorePath
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'SESARWebHook'
$ServiceDisplayName = 'Secure Exchanges - SESAR WebHook'
$EventSource = 'SESARWebHook.API.NetCore'
$ExeName = 'SESARWebHook.API.NetCore.exe'

function Write-Step { param([string] $Text) Write-Host "=== $Text ===" -ForegroundColor Cyan }
function Write-Ok { param([string] $Text) Write-Host "  [ok] $Text" -ForegroundColor Green }
function Write-Warn { param([string] $Text) Write-Host "  [!] $Text" -ForegroundColor Yellow }

# --------------------------------------------------------------------------------------
# 1. Verifications prealables
# --------------------------------------------------------------------------------------
Write-Step '1/8 Verifications prealables'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Ce script doit etre execute dans une console PowerShell ouverte en administrateur.'
}

$sourceApp = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $sourceApp $ExeName))) {
  throw "Paquet incomplet : $ExeName est introuvable dans $sourceApp"
}

Write-Ok 'Droits administrateur et contenu du paquet valides'

# Source du fichier de secrets : le chemin fourni, sinon le fichier depose a cote du script.
# S'il n'y en a nulle part, l'installation se poursuit : le fichier peut deja etre en place
# a la racine de l'application depuis une installation precedente.
$secretsSource = $null
if ($SecretsFile) {
  if (-not (Test-Path $SecretsFile)) {
    throw ("Fichier de secrets introuvable : $SecretsFile`n" +
           "Deposer connectors.secrets.json a cote de ce script et relancer sans -SecretsFile, " +
           "ou corriger le chemin.")
  }
  $secretsSource = (Resolve-Path $SecretsFile).Path
} else {
  $localSecrets = Join-Path $PSScriptRoot 'connectors.secrets.json'
  if (Test-Path $localSecrets) {
    $secretsSource = $localSecrets
    Write-Ok 'connectors.secrets.json trouve dans le dossier du script'
  }
}

# Compte de service : identite utilisee pour le service, les droits NTFS et DPAPI.
$credential = $null
if ($ServiceAccount) {
  $identity = $ServiceAccount
  Write-Host "  Mot de passe requis pour le compte $ServiceAccount"
  $credential = Get-Credential -UserName $ServiceAccount -Message "Mot de passe du compte de service $ServiceAccount"
} else {
  $identity = 'NT AUTHORITY\SYSTEM'
  Write-Warn 'Aucun -ServiceAccount fourni : le service tournera en LocalSystem, un compte tres privilegie.'
  Write-Warn 'Un compte de service dedie, avec acces au seul dossier de SESAR, est preferable.'
}

# --------------------------------------------------------------------------------------
# 2. Arret du service existant (mise a jour)
# --------------------------------------------------------------------------------------
Write-Step '2/8 Service existant'

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
  if ($existing.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', (New-TimeSpan -Seconds 30))
  }
  & sc.exe delete $ServiceName | Out-Null
  Start-Sleep -Seconds 2
  Write-Ok 'Ancien service arrete et desinscrit (les donnees sont conservees)'
} else {
  Write-Ok 'Aucune installation precedente'
}

# --------------------------------------------------------------------------------------
# 3. Copie de l'application
# --------------------------------------------------------------------------------------
Write-Step '3/8 Copie de l application'

$secretsPath = Join-Path $InstallPath 'connectors.secrets.json'

# Le fichier de secrets vit a la racine de l'application : il est mis de cote avant le
# remplacement du dossier, puis remis en place si l'installation n'en fournit pas un nouveau.
$preservedSecrets = $null
if (Test-Path $secretsPath) {
  $preservedSecrets = Join-Path ([System.IO.Path]::GetTempPath()) ('secrets_' + [Guid]::NewGuid().ToString('N'))
  Copy-Item -Path $secretsPath -Destination $preservedSecrets -Force
}

if (Test-Path $InstallPath) {
  # On remplace le code, jamais la configuration du poste ni les donnees.
  Get-ChildItem -Path $InstallPath -Force | Remove-Item -Recurse -Force
} else {
  New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}
Copy-Item -Path (Join-Path $sourceApp '*') -Destination $InstallPath -Recurse -Force
Write-Ok "Application copiee dans $InstallPath"

if ($preservedSecrets) {
  if (-not $secretsSource) {
    Copy-Item -Path $preservedSecrets -Destination $secretsPath -Force
    Write-Ok 'Fichier de secrets de l installation precedente conserve'
  }
  Remove-Item -Path $preservedSecrets -Force
}

# appsettings.json fourni par l'exploitant : il remplace celui du paquet et ses valeurs
# s'appliquent telles quelles.
if ($AppSettingsFile) {
  if (-not (Test-Path $AppSettingsFile)) {
    throw "Fichier appsettings introuvable : $AppSettingsFile"
  }
  try {
    Get-Content $AppSettingsFile -Raw | ConvertFrom-Json | Out-Null
  } catch {
    throw "Le fichier $AppSettingsFile n est pas un JSON valide : $($_.Exception.Message)"
  }
  Copy-Item -Path $AppSettingsFile -Destination (Join-Path $InstallPath 'appsettings.json') -Force
  Write-Ok 'appsettings.json fourni copie dans le dossier d application'
}

# --------------------------------------------------------------------------------------
# 4. Dossiers de donnees et droits NTFS
# --------------------------------------------------------------------------------------
Write-Step '4/8 Dossier de sortie et droits'

# -OutputPath fourni : l'exploitant impose le dossier, et il sera inscrit dans la configuration
# du poste. Sinon, on lit la valeur d'appsettings.json pour savoir ou le service ecrira, afin
# de creer ce dossier et d'y poser les bons droits sans rien imposer.
$outputImposed = $PSBoundParameters.ContainsKey('OutputPath')
if (-not $outputImposed) {
  try {
    $appSettings = Get-Content (Join-Path $InstallPath 'appsettings.json') -Raw | ConvertFrom-Json
    $fromSettings = $appSettings.Connector.filesystem.OutputPath
    if (-not [string]::IsNullOrWhiteSpace($fromSettings)) {
      $OutputPath = $fromSettings
      Write-Ok "Dossier de sortie lu dans appsettings.json : $OutputPath"
    }

    if ("$($appSettings.EnableDebugEndpoints)" -eq 'true') {
      Write-Warn 'appsettings.json active les points de terminaison de diagnostic.'
      Write-Warn 'Ils acceptent des donnees arbitraires sans authentification : a reserver a un poste de test.'
    }
  } catch {
    Write-Warn "Lecture d appsettings.json impossible, dossier par defaut conserve : $($_.Exception.Message)"
  }
}

if (-not (Test-Path $OutputPath)) { New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null }

# Le dossier de sortie contient des courriels et des pieces jointes dechiffres : l heritage
# est coupe et seuls les administrateurs et le compte de service y accedent.
& icacls $OutputPath /inheritance:r /q | Out-Null
& icacls $OutputPath /grant:r "*S-1-5-32-544:(OI)(CI)F" /q | Out-Null   # Administrateurs
& icacls $OutputPath /grant:r "*S-1-5-18:(OI)(CI)F" /q | Out-Null       # SYSTEM
if ($ServiceAccount) {
  & icacls $OutputPath /grant:r "$($ServiceAccount):(OI)(CI)M" /q | Out-Null
}
Write-Ok "Droits restreints appliques sur $OutputPath"

if ($secretsSource -and ($secretsSource -ne $secretsPath)) {
  Copy-Item -Path $secretsSource -Destination $secretsPath -Force
  Write-Ok "connectors.secrets.json place a la racine de l application"

  if ($RemoveSourceSecrets) {
    Remove-Item -Path $secretsSource -Force
    Write-Ok 'Copie source du fichier de secrets supprimee'
  } else {
    Write-Warn "Une copie en clair de vos cles subsiste ici : $secretsSource"
    Write-Warn 'Cette copie source ne sera jamais chiffree : seul le fichier du dossier'
    Write-Warn "d application l est, au premier demarrage du service."
    Write-Warn "La supprimer une fois l installation validee : Remove-Item '$secretsSource'"
  }
}

if (Test-Path $secretsPath) {
  # Le dossier d'application est lisible par tous les utilisateurs du poste. Le fichier
  # arrive en clair et n'est chiffre par DPAPI qu'au premier demarrage du service : ses
  # droits sont donc restreints des maintenant, sans attendre.
  & icacls $secretsPath /inheritance:r /q | Out-Null
  & icacls $secretsPath /grant:r "*S-1-5-32-544:F" /q | Out-Null   # Administrateurs
  & icacls $secretsPath /grant:r "*S-1-5-18:F" /q | Out-Null       # SYSTEM
  if ($ServiceAccount) {
    & icacls $secretsPath /grant:r "$($ServiceAccount):M" /q | Out-Null
  }
  Write-Ok 'Droits du fichier de secrets restreints aux administrateurs et au compte de service'

  $head = [System.IO.File]::ReadAllBytes($secretsPath) | Select-Object -First 4
  $isPlainJson = ($head -contains 0x7B)
  if (-not $isPlainJson) {
    Write-Warn 'Le fichier de secrets est deja chiffre par DPAPI.'
    Write-Warn "Il ne sera lisible que s il a ete chiffre par le compte $identity sur ce poste."
    Write-Warn 'Sinon, redeposer la version JSON en clair avant de demarrer le service.'
  }
} else {
  Write-Warn "connectors.secrets.json est absent de $InstallPath : le service demarrera mais refusera les webhooks."
}

if ($SesarStorePath) {
  if (Test-Path $SesarStorePath) {
    Write-Ok "Dossier de stockage SESAR trouve : $SesarStorePath"
    Write-Warn "Verifier que $identity a un acces en lecture sur ce dossier, sinon les pieces jointes ne pourront pas etre lues."
  } else {
    Write-Warn "Dossier de stockage SESAR introuvable : $SesarStorePath"
  }
}

# --------------------------------------------------------------------------------------
# 5. Certificat TLS
# --------------------------------------------------------------------------------------
Write-Step '5/8 Certificat TLS'

if ($CertificateThumbprint) {
  $cert = Get-ChildItem -Path Cert:\LocalMachine\My |
          Where-Object { $_.Thumbprint -eq $CertificateThumbprint.Replace(' ', '') }
  if (-not $cert) { throw "Certificat $CertificateThumbprint introuvable dans LocalMachine\My" }
  Write-Ok "Certificat fourni utilise : $($cert.Subject)"
} else {
  $cert = Get-ChildItem -Path Cert:\LocalMachine\My |
          Where-Object { $_.Subject -eq 'CN=SESARWebHook' -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
          Select-Object -First 1

  if (-not $cert) {
    # Certificat limite a la boucle locale de ce poste : l ecoute est locale, donc
    # l autorite de confiance locale suffit et la validation reste active cote SESAR.
    $cert = New-SelfSignedCertificate -Subject 'CN=SESARWebHook' `
                                      -DnsName 'localhost', $env:COMPUTERNAME `
                                      -CertStoreLocation 'Cert:\LocalMachine\My' `
                                      -FriendlyName 'Secure Exchanges - SESAR WebHook' `
                                      -KeyAlgorithm RSA -KeyLength 2048 `
                                      -HashAlgorithm SHA256 `
                                      -KeyExportPolicy NonExportable `
                                      -NotAfter (Get-Date).AddYears(3)
    Write-Ok 'Certificat auto-signe cree (RSA 2048, SHA-256, 3 ans)'
  } else {
    Write-Ok 'Certificat existant reutilise'
  }

  # Ajout aux autorites de confiance du poste pour que SESAR valide la chaine.
  $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
  $store.Open('ReadWrite')
  $already = $store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
  if (-not $already) {
    $store.Add((New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(, $cert.RawData)))
  }
  $store.Close()
  Write-Ok 'Certificat approuve sur ce poste (validation TLS active cote SESAR)'
}

# Kestrel retrouve le certificat par son nom simple (FindBySubjectName), pas par son nom
# distinctif complet : "SESARWebHook" et non "CN=SESARWebHook". C'est cette valeur qui doit
# aller dans la configuration.
$certSubjectName = $cert.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
if ([string]::IsNullOrWhiteSpace($certSubjectName)) {
  throw "Impossible de determiner le nom simple du certificat $($cert.Subject)"
}

# Un compte de service dedie doit pouvoir lire la cle privee du certificat.
if ($ServiceAccount) {
  try {
    $keyName = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
    if (-not $keyName) {
      $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
      $keyName = $rsa.Key.UniqueName
    }
    $keyFile = Get-ChildItem -Path "$env:ProgramData\Microsoft\Crypto" -Recurse -Filter $keyName -ErrorAction SilentlyContinue |
               Select-Object -First 1
    if ($keyFile) {
      & icacls $keyFile.FullName /grant "$($ServiceAccount):R" /q | Out-Null
      Write-Ok 'Acces en lecture a la cle privee accorde au compte de service'
    } else {
      Write-Warn 'Cle privee du certificat introuvable sur le disque : accorder l acces manuellement si le service ne demarre pas.'
    }
  } catch {
    Write-Warn "Acces a la cle privee non accorde automatiquement : $($_.Exception.Message)"
  }
}

# Reproduction de la recherche que fera Kestrel au demarrage : nom simple, chaine valide,
# usage Authentification du serveur et cle privee presente. Echouer ici donne un message
# clair, plutot qu un plantage du service quelques secondes plus tard.
$verifyStore = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
$verifyStore.Open('ReadOnly')
$candidates = $verifyStore.Certificates.Find(
  [System.Security.Cryptography.X509Certificates.X509FindType]::FindBySubjectName, $certSubjectName, $true)
$verifyStore.Close()

$usable = $null
foreach ($candidate in $candidates) {
  if (-not $candidate.HasPrivateKey) { continue }

  # EnhancedKeyUsages est une OidCollection : ses elements sont deja des Oid, d'ou .Value.
  # Un certificat sans extension d'usage etendu convient a tous les usages.
  $eku = $candidate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
  $hasServerAuth = (-not $eku) -or (@($eku.EnhancedKeyUsages.Value) -contains '1.3.6.1.5.5.7.3.1')

  if ($hasServerAuth) { $usable = $candidate; break }
}

if (-not $usable) {
  throw ("Le certificat '$certSubjectName' n est pas utilisable par Kestrel " +
         "($($candidates.Count) certificat(s) trouve(s) sous ce nom dans LocalMachine\My).`n" +
         "Verifier qu il est non expire, avec une chaine de confiance valide, " +
         "l usage Authentification du serveur et une cle privee accessible.")
}
Write-Ok "Certificat valide et retrouvable sous le nom '$certSubjectName'"

# --------------------------------------------------------------------------------------
# 6. Configuration du poste
# --------------------------------------------------------------------------------------
Write-Step '6/8 Configuration du poste'

# ASP.NET Core lit appsettings.json, puis appsettings.Production.json qui l'emporte.
# Ce fichier ne contient donc que ce que l'installation est seule a connaitre : le point
# d'ecoute et le certificat qu'elle vient de preparer. Tout le reste vient d'appsettings.json,
# afin qu'un fichier deja configure par l'exploitant soit respecte.
#
# Seule exception : un -OutputPath passe en ligne de commande, qui est une consigne explicite
# et prime alors sur le fichier.
$outputOverride = ''
if ($outputImposed) {
  $jsonOutputPath = (Join-Path $OutputPath '').Replace('\', '\\')
  $outputOverride = @"
,

  "Connector": {
    "filesystem": {
      "OutputPath": "$jsonOutputPath"
    }
  }
"@
}

$productionConfig = @"
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://localhost:$Port",
        "Certificate": {
          "Store": "My",
          "Location": "LocalMachine",
          "Subject": "$certSubjectName",
          "AllowInvalid": false
        }
      }
    }
  }$outputOverride
}
"@

$configPath = Join-Path $InstallPath 'appsettings.Production.json'
Set-Content -Path $configPath -Value $productionConfig -Encoding utf8
Write-Ok "Configuration ecrite dans $configPath"

# --------------------------------------------------------------------------------------
# 7. Enregistrement du service
# --------------------------------------------------------------------------------------
Write-Step '7/8 Service Windows'

if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
  New-EventLog -LogName Application -Source $EventSource
}
Write-Ok "Source du journal d evenements prete : $EventSource"

$exePath = Join-Path $InstallPath $ExeName
$newServiceArgs = @{
  Name           = $ServiceName
  BinaryPathName = "`"$exePath`""
  DisplayName    = $ServiceDisplayName
  Description    = 'Recoit les webhooks SESAR et reconstruit les courriels sur le disque local.'
  StartupType    = 'Automatic'
}
if ($credential) { $newServiceArgs['Credential'] = $credential }

New-Service @newServiceArgs | Out-Null

# L environnement ASP.NET Core doit etre Production pour que appsettings.Production.json soit lu.
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $serviceKey -Name 'Environment' -PropertyType MultiString `
                 -Value @('ASPNETCORE_ENVIRONMENT=Production') -Force | Out-Null

# Redemarrage automatique en cas d arret imprevu.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Ok 'Service enregistre, demarrage automatique et reprise sur incident configures'

Start-Service -Name $ServiceName
Write-Ok 'Service demarre'

# --------------------------------------------------------------------------------------
# 8. Verification
# --------------------------------------------------------------------------------------
Write-Step '8/8 Verification'

$healthUrl = "https://localhost:$Port/api/health/status"

# Windows PowerShell 5.1 negocie encore TLS 1.0 par defaut selon la configuration du poste,
# alors que le service n'accepte que TLS 1.2 et plus.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$status = $null
$lastError = $null
for ($i = 1; $i -le 10; $i++) {
  try {
    $status = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5
    break
  } catch {
    $lastError = $_
    Start-Sleep -Seconds 2
  }
}

if (-not $status) {
  Write-Warn "L API ne repond pas sur $healthUrl"
  if ($lastError) { Write-Warn "Derniere erreur : $($lastError.Exception.Message)" }

  $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
  if ($svc -and $svc.Status -ne 'Running') {
    Write-Warn "Le service est a l etat $($svc.Status) : il s est arrete apres le demarrage."
    Write-Warn 'Cause frequente : certificat ou configuration invalide. Le detail est dans le journal.'
  } else {
    Write-Warn "Le service tourne mais la connexion TLS echoue : verifier que le compte $identity"
    Write-Warn "a acces a la cle privee du certificat '$certSubjectName'."
  }

  Write-Warn "Consulter l Observateur d evenements, journal Application, source $EventSource"
  throw 'Installation terminee mais le service ne repond pas.'
}

Write-Ok "API joignable sur $healthUrl"

if ($status.Configuration.ProcessorInitialized) {
  Write-Ok 'Cles de dechiffrement chargees : le service accepte les webhooks'
} else {
  Write-Warn 'Les cles du webhook ne sont pas chargees : verifier connectors.secrets.json puis redemarrer le service.'
}
if (-not $status.SecretsDiagnostics.SecretsFileFound) {
  Write-Warn "Fichier de secrets introuvable a l emplacement configure : $secretsPath"
}

# Le chiffrement DPAPI a lieu a la premiere lecture, donc au demarrage du service. Un echec
# d ecriture est silencieux cote application : on verifie ici, plutot que de le supposer.
if (Test-Path $secretsPath) {
  $secretsBytes = [System.IO.File]::ReadAllBytes($secretsPath)
  $startsWithBrace = ($secretsBytes.Length -gt 0) -and ($secretsBytes[0] -eq 0x7B)
  $startsWithBom = ($secretsBytes.Length -gt 3) -and ($secretsBytes[0] -eq 0xEF) -and ($secretsBytes[3] -eq 0x7B)

  if ($startsWithBrace -or $startsWithBom) {
    Write-Warn 'Le fichier de secrets est encore en clair apres le demarrage du service.'
    Write-Warn "Verifier que $identity peut ecrire dans $InstallPath, puis redemarrer le service."
  } else {
    Write-Ok "Fichier de secrets chiffre par DPAPI sous l identite $identity"
  }
}

Write-Host ''
Write-Host 'Installation terminee.' -ForegroundColor Green
Write-Host ''
Write-Host 'A configurer dans SESAR :'
Write-Host "  WebHook = https://localhost:$Port/api/webhook/filesystem" -ForegroundColor White
Write-Host ''
Write-Host 'Emplacements :'
Write-Host "  Application       : $InstallPath"
Write-Host "  Reglages          : $(Join-Path $InstallPath 'appsettings.json')"
Write-Host "  Ecoute et certificat : $configPath"
Write-Host "  Secrets           : $secretsPath"
Write-Host "  Courriels produits: $OutputPath"
Write-Host "  Journaux          : Observateur d evenements > Application > source $EventSource"
Write-Host ''
Write-Host 'Commandes utiles :'
Write-Host "  Restart-Service $ServiceName"
Write-Host "  Get-EventLog -LogName Application -Source $EventSource -Newest 20 | Format-List"
