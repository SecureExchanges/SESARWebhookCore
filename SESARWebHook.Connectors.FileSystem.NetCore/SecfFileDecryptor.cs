using SecureExchangesSDK.Helpers;
using SESARWebHook.Core.Configuration;
using System;
using System.Collections.Generic;
using System.IO;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Dechiffre les fichiers SESAR ".secf" avec la PrivateAESKey, via le SDK Secure Exchanges.
  /// Le SDK choisit lui-meme le nom du fichier produit : le dechiffrement passe donc par un dossier
  /// temporaire prive, puis le resultat est deplace sous le nom voulu.
  /// </summary>
  internal sealed class SecfFileDecryptor
  {
    /// <summary>Extension des fichiers chiffres SESAR.</summary>
    public const string Extension = ".secf";

    /// <summary>Nom du parametre portant la cle, dans les secrets d'un connecteur ou en global.</summary>
    public const string PrivateAesKeySettingName = "PrivateAESKey";

    private readonly byte[] _key;
    private readonly byte[] _iv;

    /// <summary>Cle et IV peuvent etre null : le dechiffrement est alors refuse avec un message clair.</summary>
    public SecfFileDecryptor(byte[] key, byte[] iv)
    {
      _key = key;
      _iv = iv;
    }

    /// <summary>Vrai quand une cle est configuree.</summary>
    public bool HasKey => _key != null && _iv != null;

    /// <summary>
    /// Construit le dechiffreur a partir des parametres d'un connecteur : priorite a la PrivateAESKey
    /// propre au connecteur (connectors.secrets.json, section du connecteur), repli sur la cle globale
    /// des secrets. Sans cle, le dechiffreur est cree mais refusera tout .secf.
    /// Une cle mal formee leve une InvalidOperationException : mieux vaut refuser de demarrer.
    /// </summary>
    public static SecfFileDecryptor FromSettings(Dictionary<string, string> settings)
    {
      string privateAesKey = null;
      if (settings != null)
      {
        settings.TryGetValue(PrivateAesKeySettingName, out privateAesKey);
      }

      if (string.IsNullOrWhiteSpace(privateAesKey))
      {
        privateAesKey = SecureConfigManager.GetPrivateAESKey();
      }

      if (string.IsNullOrWhiteSpace(privateAesKey))
      {
        return new SecfFileDecryptor(null, null);
      }

      ParsePrivateAesKey(privateAesKey, out var key, out var iv);
      return new SecfFileDecryptor(key, iv);
    }

    /// <summary>Decode une PrivateAESKey au format SESAR "base64key_base64iv".</summary>
    public static void ParsePrivateAesKey(string privateAesKey, out byte[] key, out byte[] iv)
    {
      var parts = (privateAesKey ?? string.Empty).Split('_');
      if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
      {
        throw new InvalidOperationException("PrivateAESKey must be in format 'base64key_base64iv' separated by underscore");
      }

      try
      {
        key = Convert.FromBase64String(parts[0]);
        iv = Convert.FromBase64String(parts[1]);
      }
      catch (FormatException ex)
      {
        throw new InvalidOperationException("PrivateAESKey must be in format 'base64key_base64iv' with valid base64 strings", ex);
      }
    }

    /// <summary>
    /// Dechiffre <paramref name="encryptedPath"/> vers <paramref name="targetPath"/>.
    /// Le dossier temporaire, cree sous <paramref name="workFolder"/>, est toujours supprime, meme
    /// en cas d'erreur, pour ne laisser aucun contenu en clair residuel.
    /// </summary>
    public void DecryptTo(string encryptedPath, string workFolder, string targetPath)
    {
      if (!HasKey)
      {
        throw new InvalidOperationException("encrypted file found but no PrivateAESKey is configured");
      }

      var tempFolder = Path.Combine(workFolder, ".decrypt_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(tempFolder);

      try
      {
        if (!CryptoHelper.DecryptSecfFile(encryptedPath, tempFolder, _key, _iv))
        {
          throw new InvalidOperationException("DecryptSecfFile returned false (wrong key/IV or corrupted file)");
        }

        var produced = Directory.GetFiles(tempFolder);
        if (produced.Length == 0)
        {
          throw new FileNotFoundException("DecryptSecfFile produced no output file");
        }

        if (produced.Length == 1)
        {
          File.Move(produced[0], targetPath);
          return;
        }

        // Cas inattendu : plusieurs fichiers produits. On les garde tous, sous des noms surs.
        var targetFolder = Path.GetDirectoryName(targetPath);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(targetPath) };
        foreach (var p in produced)
        {
          var name = SafePaths.MakeUniqueFileName(targetFolder, SafePaths.SanitizeFileName(Path.GetFileName(p)), usedNames);
          File.Move(p, SafePaths.GetSafeDestinationPath(targetFolder, name));
        }
      }
      finally
      {
        FileSystemCleanup.TryDeleteDirectory(tempFolder);
      }
    }
  }
}
