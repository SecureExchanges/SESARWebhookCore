using SecureExchangesSDK.Models.Messenging;
using System;
using System.Collections.Generic;
using System.IO;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Livre les fichiers d'un manifest SESAR dans un dossier : copie du fichier s'il existe en
  /// clair, sinon dechiffrement de sa version ".secf". Un fichier en echec n'interrompt pas les
  /// autres ; les echecs sont accumules dans le <see cref="DeliveryReport"/>.
  /// </summary>
  internal sealed class ManifestFileDeliverer
  {
    private readonly SecfFileDecryptor _decryptor;

    public ManifestFileDeliverer(SecfFileDecryptor decryptor)
    {
      _decryptor = decryptor ?? throw new ArgumentNullException(nameof(decryptor));
    }

    /// <summary>Livre les fichiers du manifest et retourne un nouveau rapport.</summary>
    public DeliveryReport Deliver(StoreManifest manifest, string filesFolder)
    {
      var report = new DeliveryReport();
      Deliver(manifest, filesFolder, report);
      return report;
    }

    /// <summary>
    /// Livre chaque entree de manifest.FilesLocation dans <paramref name="filesFolder"/> et alimente
    /// <paramref name="report"/>. Le dossier n'est cree que si le manifest reference au moins un fichier.
    /// </summary>
    public void Deliver(StoreManifest manifest, string filesFolder, DeliveryReport report)
    {
      if (report == null)
      {
        throw new ArgumentNullException(nameof(report));
      }

      var locations = manifest?.FilesLocation;
      if (locations == null || locations.Count == 0)
      {
        return;
      }

      report.TotalFiles = locations.Count;
      Directory.CreateDirectory(filesFolder);
      var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      for (int i = 0; i < locations.Count; i++)
      {
        var sourcePath = ResolveSourcePath(manifest, i);

        try
        {
          if (string.IsNullOrWhiteSpace(sourcePath))
          {
            throw new FileNotFoundException("manifest entry has neither FullPath nor FileName");
          }

          var targetName = SafePaths.MakeUniqueFileName(filesFolder, ResolveTargetFileName(manifest, i, sourcePath), usedNames);
          var targetPath = SafePaths.GetSafeDestinationPath(filesFolder, targetName);

          if (File.Exists(sourcePath))
          {
            File.Copy(sourcePath, targetPath, overwrite: false);
            report.Copied++;
          }
          else if (File.Exists(sourcePath + SecfFileDecryptor.Extension))
          {
            _decryptor.DecryptTo(sourcePath + SecfFileDecryptor.Extension, filesFolder, targetPath);
            report.Decrypted++;
          }
          else
          {
            throw new FileNotFoundException($"neither '{sourcePath}' nor its '{SecfFileDecryptor.Extension}' version exists. Content file not found. TODO: implement .see file extension");
          }

          report.DeliveredPaths.Add(targetPath);
          report.TotalSize += new FileInfo(targetPath).Length;
        }
        catch (Exception ex)
        {
          report.Failed++;
          report.AddError($"File #{i} ({Path.GetFileName(sourcePath ?? string.Empty)})", ex);
        }
      }
    }

    /// <summary>
    /// Chemin source du fichier : FullPath de l'entree quand SESAR l'a rempli, sinon
    /// DirectoryPath + FileName. SESAR laisse FullPath vide (et IsDownloaded a false) pour un
    /// fichier dont le contenu est identique a un autre du meme envoi, alors que son .secf est bien
    /// present dans le dossier de l'echange. Retourne null si aucun chemin ne peut etre construit.
    /// </summary>
    private static string ResolveSourcePath(StoreManifest manifest, int index)
    {
      var location = manifest.FilesLocation[index];
      if (location == null)
      {
        return null;
      }

      if (!string.IsNullOrWhiteSpace(location.FullPath))
      {
        return location.FullPath;
      }

      var fileName = SafePaths.SanitizeFileName(location.FileName);
      if (string.IsNullOrWhiteSpace(manifest.DirectoryPath) || string.IsNullOrEmpty(fileName))
      {
        return null;
      }

      return Path.Combine(manifest.DirectoryPath, fileName);
    }

    /// <summary>
    /// Nom visible du fichier livre : RealFileName des metadonnees, sinon FileName de l'entree,
    /// sinon le nom contenu dans FullPath. Toujours reduit a un nom de fichier nu.
    /// </summary>
    private static string ResolveTargetFileName(StoreManifest manifest, int index, string sourcePath)
    {
      string candidate = null;

      var metaData = manifest.FilesMetaData;
      if (metaData != null && index < metaData.Count && metaData[index] != null)
      {
        candidate = metaData[index].RealFileName;
      }

      if (string.IsNullOrWhiteSpace(candidate))
      {
        candidate = manifest.FilesLocation[index].FileName;
      }

      if (string.IsNullOrWhiteSpace(candidate))
      {
        candidate = Path.GetFileName(sourcePath);
      }

      var sanitized = SafePaths.SanitizeFileName(candidate);
      return string.IsNullOrEmpty(sanitized) ? $"file_{index}" : sanitized;
    }
  }
}
