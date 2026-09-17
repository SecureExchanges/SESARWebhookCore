using System.IO;
using System.Linq;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Operations de nettoyage "au mieux" : elles ne levent jamais d'exception, pour que l'erreur du
  /// traitement principal, s'il y en a une, reste celle qui est rapportee.
  /// </summary>
  internal static class FileSystemCleanup
  {
    /// <summary>Supprime le dossier s'il existe et ne contient rien.</summary>
    public static void RemoveFolderIfEmpty(string folder)
    {
      try
      {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
          Directory.Delete(folder);
        }
      }
      catch
      {
        // Un dossier vide qui reste n'est pas une erreur de traitement
      }
    }

    /// <summary>Supprime un fichier s'il existe.</summary>
    public static void TryDeleteFile(string path)
    {
      try
      {
        if (File.Exists(path))
        {
          File.Delete(path);
        }
      }
      catch
      {
        // Nettoyage au mieux
      }
    }

    /// <summary>Supprime un dossier et tout son contenu s'il existe.</summary>
    public static void TryDeleteDirectory(string path)
    {
      try
      {
        if (Directory.Exists(path))
        {
          Directory.Delete(path, recursive: true);
        }
      }
      catch
      {
        // Nettoyage au mieux
      }
    }
  }
}
