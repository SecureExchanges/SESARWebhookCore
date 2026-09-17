# SESAR WebHook - Guide d'installation

Ce paquet installe un service Windows qui reçoit les webhooks de SESAR et reconstruit chaque
échange en fichier `.eml` sur le disque local, pièces jointes déchiffrées incluses.

## Prérequis

- Windows 10, Windows 11 ou Windows Server 2016 et plus récent, 64 bits.
- Droits administrateur local sur le poste.
- SESAR installé et fonctionnel sur le même poste.
- Votre fichier `connectors.secrets.json`, contenant vos clés Secure Exchanges.
- Aucun runtime .NET à installer: l'application est autonome.

## Contenu du paquet

| Élément | Rôle |
|---|---|
| `app\` | L'application et ses dépendances |
| `Install-SESARWebHook.ps1` | Script d'installation |
| `Uninstall-SESARWebHook.ps1` | Script de désinstallation |
| `connectors.secrets.template.json` | Gabarit du fichier de secrets |
| `INSTALL.md` | Ce guide |

## Étape 1. Vérifier l'intégrité de l'archive

Secure Exchanges vous transmet l'empreinte SHA-256 de l'archive par un canal distinct.
Comparez-la avant de décompresser.

```powershell
Get-FileHash .\SESARWebHook-<version>.zip -Algorithm SHA256
```

Si les empreintes diffèrent, n'installez pas et contactez Secure Exchanges.

## Étape 2. Décompresser le paquet

Décompressez l'archive dans un dossier temporaire, par exemple `C:\Install\SESARWebHook`.
Windows bloque les fichiers téléchargés: débloquez-les avant d'exécuter les scripts.

```powershell
Get-ChildItem -Path C:\Install\SESARWebHook -Recurse | Unblock-File
```

## Étape 3. Préparer le fichier de secrets

Le fichier `connectors.secrets.json` contient vos clés. Il ne doit jamais être transmis par
courriel ni déposé dans un dépôt de code. Il contient au minimum:

```json
{
  "WebHookEncryptionKey": "<clé webhook en base64>",
  "WebHookEncryptionIV": "<IV webhook en base64>",
  "PrivateAESKey": "<clé fichiers base64>_<IV fichiers base64>"
}
```

Les deux premières valeurs déchiffrent les appels de SESAR. La troisième déchiffre les pièces
jointes. Elles proviennent de votre configuration SESAR.

**Déposez ce fichier en clair dans le dossier décompressé**, à côté de
`Install-SESARWebHook.ps1`. Le script le trouvera tout seul et le placera à la racine du dossier
d'application, où le service le cherche. Aucun chemin n'est à configurer.

Le script restreint aussitôt ses droits aux administrateurs et au compte de service. Au premier
démarrage, le service le chiffre sur place avec Windows DPAPI et il devient illisible pour tout
autre compte. Lors d'une mise à jour, le fichier déjà en place est conservé.

Le chiffrement ne touche que le fichier installé. La copie que vous avez déposée dans le dossier
décompressé reste en clair, puisque plus personne ne la lit. Supprimez-la une fois l'installation
validée, ou lancez l'installation avec `-RemoveSourceSecrets` pour qu'elle le fasse.

## Étape 4. Lancer l'installation

Le script ne s'exécute pas par double-clic: Windows ouvrirait le fichier dans le Bloc-notes.
Il faut passer par une console.

1. Menu Démarrer, taper `PowerShell`, clic droit sur Windows PowerShell, **Exécuter en tant
   qu'administrateur**.
2. Se placer dans le dossier décompressé, puis lancer le script.

```powershell
Set-Location C:\Install\SESARWebHook
.\Install-SESARWebHook.ps1
```

Si la stratégie d'exécution de votre poste bloque les scripts, lancez-le sous cette forme, qui
n'autorise que ce fichier pour cette session:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-SESARWebHook.ps1
```

Variante recommandée, avec un compte de service dédié plutôt que LocalSystem. Le script
demande le mot de passe dans une invite Windows et ne l'enregistre nulle part.

```powershell
.\Install-SESARWebHook.ps1 -ServiceAccount "DOMAINE\svc_sesarwebhook" -SesarStorePath "D:\SESAR\Store"
```

Si votre fichier de secrets se trouve ailleurs, indiquez son chemin:

```powershell
.\Install-SESARWebHook.ps1 -SecretsFile D:\Secrets\connectors.secrets.json
```

Si vous avez déjà un `appsettings.json` configuré à votre façon, fournissez-le. Ses valeurs sont
reprises telles quelles: dossier de sortie, connecteurs actifs, journalisation. L'installation
n'impose que le point d'écoute et le certificat.

```powershell
.\Install-SESARWebHook.ps1 -AppSettingsFile D:\Config\appsettings.json
```

Vous pouvez aussi simplement remplacer `app\appsettings.json` dans le dossier décompressé avant
de lancer l'installation. Le résultat est le même.

Le script effectue huit étapes, affichées à l'écran: vérifications, arrêt d'une version
précédente, copie de l'application, création des dossiers avec droits restreints, certificat
TLS, configuration du poste, enregistrement du service, vérification.

### Paramètres disponibles

| Paramètre | Défaut | Rôle |
|---|---|---|
| `-InstallPath` | `C:\Program Files\SESARWebHook` | Application et fichier de secrets |
| `-DataPath` | `C:\SESARWebHook` | Dossier parent par défaut des courriels |
| `-OutputPath` | valeur d'`appsettings.json` | Impose le dossier des courriels reconstruits |
| `-AppSettingsFile` | aucun | Votre `appsettings.json` déjà configuré |
| `-SecretsFile` | le fichier à côté du script | Votre `connectors.secrets.json` |
| `-RemoveSourceSecrets` | désactivé | Supprime la copie en clair après installation |
| `-Port` | `5001` | Port HTTPS, en boucle locale |
| `-CertificateThumbprint` | aucun | Certificat existant à utiliser |
| `-ServiceAccount` | LocalSystem | Compte d'exécution du service |
| `-SesarStorePath` | aucun | Dossier de stockage de SESAR, vérifié |

## Étape 5. Configurer SESAR

Dans la configuration SESAR, indiquez l'adresse du webhook affichée en fin d'installation:

```
https://localhost:5001/api/webhook/filesystem
```

Redémarrez SESAR pour qu'il prenne l'adresse en compte.

## Étape 6. Valider de bout en bout

1. Vérifiez l'état du service.

```powershell
Invoke-RestMethod https://localhost:5001/api/health/status
```

`ProcessorInitialized` et `SecretsFileFound` doivent valoir `True`.

2. Envoyez un courriel de test avec une pièce jointe depuis SESAR.

3. Vérifiez qu'un fichier `.eml` apparaît dans `C:\SESARWebHook\Output\<date>\`, nommé d'après
le sujet et la date d'envoi. Ouvrez-le: le message et ses pièces jointes doivent être lisibles.

## Exploitation

**Où sont les journaux.** Observateur d'événements, journal Application, source
`SESARWebHook.API.NetCore`. Chaque échange traité et chaque échec y sont consignés.

```powershell
Get-EventLog -LogName Application -Source SESARWebHook.API.NetCore -Newest 20 | Format-List
```

**Redémarrer le service** après une modification de configuration ou du fichier de secrets.

```powershell
Restart-Service SESARWebHook
```

**Changer un réglage.** Les réglages vivent dans `appsettings.json`, dans le dossier
d'application. Modifiez-le, puis redémarrez le service. L'installation ne le remplace pas si
vous fournissez le vôtre.

Le fichier voisin `appsettings.Production.json` ne contient que le point d'écoute et le
certificat, que l'installation prépare elle-même. Il l'emporte sur `appsettings.json` pour ces
deux éléments seulement, ainsi que sur le dossier de sortie si vous avez passé `-OutputPath`.

**Fichiers volumineux.** Quand le total des pièces jointes d'un échange dépasse 2 Go, elles ne
sont pas jointes au `.eml`. Elles sont déposées dans un dossier portant le même nom que le
`.eml`, à côté de celui-ci.

**Espace disque.** Les courriels produits s'accumulent dans le dossier de sortie. Prévoyez une
rétention et une purge selon vos obligations de conservation.

## Mise à jour

Relancez `Install-SESARWebHook.ps1` depuis le nouveau paquet, avec les mêmes paramètres. Le
script arrête le service, remplace l'application et le redémarre. Vos données et votre fichier
de secrets sont conservés.

## Désinstallation

```powershell
.\Uninstall-SESARWebHook.ps1
```

Le dossier d'application est supprimé, fichier de secrets compris. Les courriels produits sont
conservés. Ajoutez `-RemoveData` pour les supprimer définitivement, et `-RemoveCertificate` pour
retirer le certificat.

## Dépannage

| Symptôme | Cause probable | Correctif |
|---|---|---|
| `ProcessorInitialized: False` | Fichier de secrets absent ou illisible | Redéposer le fichier en clair à la racine du dossier d'application, puis redémarrer le service |
| Le service démarre puis s'arrête | Fichier de secrets chiffré par un autre compte | Redéposer la version en clair, redémarrer |
| SESAR signale une erreur 500 | Un fichier de l'échange n'a pas pu être livré | Consulter l'Observateur d'événements pour le détail |
| SESAR ne joint pas le service | Port occupé, ou SESAR sur un autre poste | Vérifier le port, voir la note ci-dessous |
| Le dossier de sortie ignore `appsettings.json` | L'installation a été lancée avec `-OutputPath`, qui l'emporte | Réinstaller sans ce paramètre, ou retirer la section du fichier `appsettings.Production.json` |
| Le port ou le certificat ignorent `appsettings.json` | Ils viennent de `appsettings.Production.json`, par conception | Utiliser `-Port` et `-CertificateThumbprint` à l'installation |
| Erreur de certificat côté SESAR | Certificat non approuvé | Relancer l'installation, qui approuve le certificat sur ce poste |

**SESAR sur un autre poste.** L'installation par défaut n'écoute qu'en boucle locale, ce qui
protège le service de toute sollicitation venue du réseau. Si SESAR tourne sur un autre poste,
il faut un certificat émis pour le nom réseau du serveur, une adresse d'écoute correspondante
et une règle de pare-feu limitée à l'adresse de SESAR. Contactez Secure Exchanges pour cette
configuration.

## Sécurité

- Le dossier de données contient des courriels et des pièces jointes **déchiffrés**.
  L'installation coupe l'héritage des droits et ne laisse l'accès qu'aux administrateurs et au
  compte de service. Ne relâchez pas ces droits.
- Le fichier de secrets se trouve à la racine du dossier d'application. Ses droits sont
  restreints dès la copie, avant même son chiffrement par DPAPI au premier démarrage.
- Le fichier de secrets est chiffré par DPAPI au premier démarrage, avec la portée
  `CurrentUser`. Copié sur un autre poste ou lu par un autre compte, il est inutilisable.
- Le service n'écoute qu'en boucle locale et refuse tout appel dont la clé de contrôle ne
  correspond pas à votre configuration SESAR.
- Les réponses d'erreur renvoyées à l'appelant restent génériques. Le détail technique n'existe
  que dans le journal local.
- Les points de terminaison de diagnostic sont désactivés dans ce paquet. Ne les activez que sur
  un poste de test: ils acceptent des données arbitraires sans authentification.
