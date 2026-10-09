using SecureExchangesSDK.Models.Messenging;
using System;
using System.IO;
using System.Linq;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Supprime le dossier d'echange SESAR (manifest.DirectoryPath, manifest inclus) une fois le
  /// courriel transfere. Le chemin vient du manifest, donc de l'exterieur : la suppression est
  /// recursive et n'est faite que si toutes les conditions suivantes tiennent :
  ///  - chemin absolu, existant, qui n'est ni une racine de lecteur ni un dossier de premier niveau ;
  ///  - si SesarStoreRoot est configure, dossier strictement sous cette racine ;
  ///  - sinon, tout fichier du manifest muni d'un FullPath doit se trouver sous ce dossier.
  /// En cas de doute, rien n'est supprime et la raison est retournee pour le journal.
  /// </summary>
  internal static class SesarExchangeCleanup
  {
    /// <summary>Profondeur minimale sous la racine du lecteur : "C:\a\b" passe, "C:\a" non.</summary>
    private const int MinDepthBelowRoot = 2;

    /// <summary>Tente la suppression ; retourne false avec la raison quand elle est refusee ou echoue.</summary>
    public static bool TryDeleteExchangeFolder(StoreManifest manifest, ForwardOptions options, out string reason)
    {
      if (options == null)
      {
        throw new ArgumentNullException(nameof(options));
      }

      reason = null;
      var raw = manifest?.DirectoryPath;

      if (string.IsNullOrWhiteSpace(raw))
      {
        reason = "manifest has no DirectoryPath";
        return false;
      }

      string folder;
      try
      {
        if (!Path.IsPathRooted(raw))
        {
          reason = "DirectoryPath is not an absolute path";
          return false;
        }

        folder = NormalizeFolder(raw);
      }
      catch (ArgumentException)
      {
        reason = "DirectoryPath is not a valid path";
        return false;
      }

      if (!Directory.Exists(folder))
      {
        reason = "DirectoryPath does not exist (already removed?)";
        return false;
      }

      var root = Path.GetPathRoot(folder) ?? string.Empty;
      var depth = folder.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;
      if (depth < MinDepthBelowRoot)
      {
        reason = "DirectoryPath is too close to the drive root to be deleted safely";
        return false;
      }

      if (options.SesarStoreRoot != null)
      {
        if (!IsStrictlyUnder(folder, options.SesarStoreRoot))
        {
          reason = "DirectoryPath is outside SesarStoreRoot";
          return false;
        }
      }
      else if (manifest.FilesLocation != null)
      {
        var outside = manifest.FilesLocation
          .Where(l => l != null && !string.IsNullOrWhiteSpace(l.FullPath))
          .Select(l => l.FullPath)
          .FirstOrDefault(p => !IsUnder(p, folder));

        if (outside != null)
        {
          reason = "a manifest file lies outside DirectoryPath; refusing to delete";
          return false;
        }
      }

      try
      {
        Directory.Delete(folder, recursive: true);
        return true;
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
      {
        reason = $"{ex.GetType().Name}: {ex.Message}";
        return false;
      }
    }

    /// <summary>Chemin complet sans separateur final.</summary>
    private static string NormalizeFolder(string path)
    {
      var full = Path.GetFullPath(path);
      var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      return trimmed.Length == 0 ? full : trimmed;
    }

    /// <summary>Vrai si <paramref name="path"/> est sous <paramref name="folder"/> ou egal a lui.</summary>
    private static bool IsUnder(string path, string folder)
    {
      try
      {
        var full = Path.GetFullPath(path);
        var prefix = folder + Path.DirectorySeparatorChar;
        return string.Equals(full, folder, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
      }
      catch (ArgumentException)
      {
        return false;
      }
    }

    /// <summary>Vrai si <paramref name="folder"/> est sous <paramref name="root"/>, sans lui etre egal.</summary>
    private static bool IsStrictlyUnder(string folder, string root)
    {
      var normalizedRoot = NormalizeFolder(root);
      return !string.Equals(folder, normalizedRoot, StringComparison.OrdinalIgnoreCase) && IsUnder(folder, normalizedRoot);
    }
  }
}
