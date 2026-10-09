using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Construction sure de noms et de chemins a partir de valeurs venues du manifest (sujet, noms de
  /// fichiers). Defense contre la traversee de repertoire et l'ecrasement de fichiers existants.
  /// </summary>
  internal static class SafePaths
  {
    /// <summary>
    /// Reduit une chaine a un nom de fichier nu : aucun composant de repertoire, caracteres invalides
    /// et de controle remplaces par "_", ni "." ni "..", ni point ou espace final.
    /// Retourne une chaine vide si rien d'utilisable ne reste.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
      if (string.IsNullOrWhiteSpace(name))
      {
        return string.Empty;
      }

      var bare = name.Replace('\\', '/');
      bare = bare.Substring(bare.LastIndexOf('/') + 1);

      var invalid = Path.GetInvalidFileNameChars();
      var chars = bare.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
      var cleaned = new string(chars).Trim().TrimEnd('.', ' ');

      return cleaned == "." || cleaned == ".." ? string.Empty : cleaned;
    }

    /// <summary>
    /// Evite l'ecrasement : si le nom est deja pris dans le dossier ou dans le lot courant
    /// (<paramref name="usedNames"/>), suffixe " (n)" avant l'extension. Le nom retenu est ajoute au lot.
    /// </summary>
    public static string MakeUniqueFileName(string folder, string fileName, HashSet<string> usedNames)
    {
      var stem = Path.GetFileNameWithoutExtension(fileName);
      var ext = Path.GetExtension(fileName);
      var candidate = fileName;
      int n = 1;

      while (usedNames.Contains(candidate) || File.Exists(Path.Combine(folder, candidate)))
      {
        candidate = $"{stem} ({n++}){ext}";
      }

      usedNames.Add(candidate);
      return candidate;
    }

    /// <summary>
    /// Combine dossier et nom, puis verifie que le chemin resolu reste dans le dossier.
    /// Leve UnauthorizedAccessException sinon.
    /// </summary>
    public static string GetSafeDestinationPath(string folder, string fileName)
    {
      var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
      var fullPath = Path.GetFullPath(Path.Combine(fullFolder, fileName));

      if (!fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase))
      {
        throw new UnauthorizedAccessException("Resolved destination path escapes the output folder");
      }

      return fullPath;
    }
  }
}
