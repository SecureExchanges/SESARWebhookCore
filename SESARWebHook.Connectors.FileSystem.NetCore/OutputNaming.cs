using SecureExchangesSDK.Models.Messenging;
using System;
using System.IO;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Nommage des sorties d'un echange : le .eml et son dossier de fichiers partagent un meme nom
  /// de base, "&lt;sujet&gt; - &lt;date d'envoi yyyy-MM-dd HH-mm&gt;", unique dans le dossier de sortie.
  /// </summary>
  internal static class OutputNaming
  {
    /// <summary>Extension du courriel reconstruit.</summary>
    public const string EmlExtension = ".eml";

    /// <summary>Longueur maximale du sujet dans un nom de fichier.</summary>
    private const int MaxSubjectLength = 120;

    /// <summary>
    /// Nom de base tire du manifest. Sujet absent : "message". Date d'envoi absente : date de traitement.
    /// Le sujet est reduit a un nom de fichier nu par <see cref="SafePaths.SanitizeFileName"/>.
    /// </summary>
    public static string BuildBaseName(StoreManifest manifest)
    {
      var info = manifest?.OriginalRecipientInfo;

      var subject = SafePaths.SanitizeFileName(info?.Subject);
      if (string.IsNullOrEmpty(subject))
      {
        subject = "message";
      }
      else if (subject.Length > MaxSubjectLength)
      {
        subject = subject.Substring(0, MaxSubjectLength).TrimEnd('.', ' ');
      }

      var sentOn = info != null && info.CreateOn != default(DateTime) ? info.CreateOn : DateTime.Now;

      return $"{subject} - {sentOn:yyyy-MM-dd HH-mm}";
    }

    /// <summary>
    /// Garantit l'unicite du nom de base dans le dossier de sortie : ni un .eml ni un dossier de ce
    /// nom ne doivent deja exister. Sinon, suffixe " (1)", " (2)", ... Le .eml et son dossier
    /// recoivent ainsi toujours le meme suffixe.
    /// </summary>
    public static string MakeUniqueBaseName(string outputFolder, string baseName)
    {
      var candidate = baseName;
      int n = 1;

      while (File.Exists(Path.Combine(outputFolder, candidate + EmlExtension)) ||
             Directory.Exists(Path.Combine(outputFolder, candidate)))
      {
        candidate = $"{baseName} ({n++})";
      }

      return candidate;
    }
  }
}
