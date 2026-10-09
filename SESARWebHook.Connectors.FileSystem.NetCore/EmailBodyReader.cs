using SecureExchangesSDK.Models.Messenging;
using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Lit le corps HTML du courriel d'un echange SESAR (EmailSent.html dans manifest.DirectoryPath,
  /// en clair ou chiffre en .secf) et en extrait le message original, sans le bloc resume
  /// From/To/Subject/Date ni la copie rendue que SESAR place devant.
  /// </summary>
  internal sealed class EmailBodyReader
  {
    /// <summary>Nom du fichier HTML du courriel envoye, dans manifest.DirectoryPath.</summary>
    public const string EmailHtmlFileName = "EmailSent.html";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Structure d'un EmailSent.html SESAR : un bloc resume (From, To, Subject, Date), une copie
    /// rendue du message (groupe "rendered"), puis le marqueur
    /// "&lt;hr/&gt;ORIGINAL HTML ENCODED MESSAGE BELOW&lt;hr/&gt;&lt;br /&gt;", suivi du message
    /// original, tantot en HTML brut, tantot encode en entites HTML (&amp;lt;p&amp;gt;...).
    /// Delai impose pour se proteger d'un HTML pathologique.
    /// </summary>
    private static readonly Regex OriginalMessageMarker = new Regex(
      @"^(?<rendered>.*?)<hr\s*/?>\s*ORIGINAL\s+HTML\s+ENCODED\s+MESSAGE\s+BELOW\s*(?:<hr\s*/?>)?\s*(?:<br\s*/?>)?\s*",
      RegexOptions.Singleline | RegexOptions.IgnoreCase,
      RegexTimeout);

    /// <summary>
    /// Bloc resume place par SESAR en tete du HTML : une suite de paragraphes
    /// "&lt;p&gt;&lt;strong&gt;From&lt;/strong&gt;: ...&lt;/p&gt;" pour From, To, Subject et Date.
    /// </summary>
    private static readonly Regex SummaryHeaderBlock = new Regex(
      @"^\s*(?:<p\b[^>]*>\s*<strong>\s*(?:From|To|Subject|Date)\s*</strong>.*?</p>\s*)+",
      RegexOptions.Singleline | RegexOptions.IgnoreCase,
      RegexTimeout);

    private readonly SecfFileDecryptor _decryptor;

    /// <param name="decryptor">Sert a lire un EmailSent.html chiffre.</param>
    public EmailBodyReader(SecfFileDecryptor decryptor)
    {
      _decryptor = decryptor ?? throw new ArgumentNullException(nameof(decryptor));
    }

    /// <summary>
    /// Retourne le HTML du message original, ou null si aucun corps n'est disponible. Un probleme
    /// de lecture ou de dechiffrement est consigne dans <paramref name="report"/> et retourne null.
    /// Le dechiffrement passe par un dossier temporaire sous <paramref name="workFolder"/>,
    /// toujours supprime ensuite.
    /// </summary>
    public string Load(StoreManifest manifest, string workFolder, DeliveryReport report)
    {
      if (report == null)
      {
        throw new ArgumentNullException(nameof(report));
      }

      var directory = manifest?.DirectoryPath;
      if (string.IsNullOrWhiteSpace(directory))
      {
        return null;
      }

      var htmlPath = Path.Combine(directory, EmailHtmlFileName);
      string html = null;

      try
      {
        if (File.Exists(htmlPath))
        {
          html = File.ReadAllText(htmlPath);
        }
        else if (File.Exists(htmlPath + SecfFileDecryptor.Extension))
        {
          var tempFolder = Path.Combine(workFolder, ".decrypt_" + Guid.NewGuid().ToString("N"));
          Directory.CreateDirectory(tempFolder);
          try
          {
            var tempHtml = Path.Combine(tempFolder, EmailHtmlFileName);
            _decryptor.DecryptTo(htmlPath + SecfFileDecryptor.Extension, tempFolder, tempHtml);
            html = File.ReadAllText(tempHtml);
          }
          finally
          {
            FileSystemCleanup.TryDeleteDirectory(tempFolder);
          }
        }
      }
      catch (Exception ex)
      {
        report.AddError($"Email body ({EmailHtmlFileName})", ex);
        return null;
      }

      return html == null ? null : ExtractOriginalMessage(html);
    }

    /// <summary>
    /// Retourne le HTML du courriel original, sans le bloc resume From/To/Subject/Date.
    ///
    /// 1. Avec marqueur et contenu apres lui : ce contenu est le message original. S'il est encode en
    ///    entites HTML (cas ou SESAR affiche "&amp;lt;p&amp;gt;..." au lieu de balises), il est decode.
    /// 2. Sinon : la copie rendue qui precede le marqueur (ou tout le HTML s'il n'y a pas de
    ///    marqueur), debarrassee du bloc resume.
    /// </summary>
    public static string ExtractOriginalMessage(string html)
    {
      if (html == null)
      {
        return null;
      }

      var match = OriginalMessageMarker.Match(html);

      if (match.Success)
      {
        var original = html.Substring(match.Length).Trim();
        if (original.Length > 0)
        {
          return LooksHtmlEncoded(original) ? WebUtility.HtmlDecode(original) : original;
        }
      }

      var rendered = match.Success ? match.Groups["rendered"].Value : html;
      return SummaryHeaderBlock.Replace(rendered, string.Empty, 1);
    }

    /// <summary>
    /// Vrai quand le fragment est du HTML encode en entites plutot que du HTML brut : il contient
    /// des "&amp;lt;" et au moins autant que de vrais "&lt;". Decoder du HTML brut casserait ses
    /// "&amp;amp;", d'ou ce test avant de decoder.
    /// </summary>
    private static bool LooksHtmlEncoded(string fragment)
    {
      var encodedTags = CountOccurrences(fragment, "&lt;");
      if (encodedTags == 0)
      {
        return false;
      }

      var rawTags = CountOccurrences(fragment, "<");
      return encodedTags >= rawTags;
    }

    private static int CountOccurrences(string text, string value)
    {
      int count = 0;
      for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
      {
        count++;
      }
      return count;
    }
  }
}
