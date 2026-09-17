using MimeKit;
using SecureExchangesSDK.Models.Messenging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Reconstruit le courriel d'un echange SESAR en fichier .eml (MimeKit) : corps depuis
  /// EmailSent.html, expediteur, destinataires, sujet et date depuis le manifest, et les fichiers
  /// livres en pieces jointes quand leur total respecte la limite.
  /// </summary>
  internal sealed class EmlBuilder
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
    private readonly long _maxAttachmentsTotalSize;

    /// <param name="decryptor">Sert a lire un EmailSent.html chiffre.</param>
    /// <param name="maxAttachmentsTotalSize">Total maximal, en octets, des fichiers attaches au .eml.</param>
    public EmlBuilder(SecfFileDecryptor decryptor, long maxAttachmentsTotalSize)
    {
      _decryptor = decryptor ?? throw new ArgumentNullException(nameof(decryptor));
      _maxAttachmentsTotalSize = maxAttachmentsTotalSize;
    }

    /// <summary>
    /// Ecrit le .eml a <paramref name="emlPath"/>. Les fichiers livres sont attaches si leur total
    /// respecte la limite ; sinon ils restent dans <paramref name="filesFolder"/> et le .eml ne porte
    /// que le corps. Rien n'est ecrit quand le manifest n'a ni information de courriel ni fichier livre.
    /// En cas d'echec du .eml, les fichiers livres sont conserves dans leur dossier.
    /// </summary>
    public void Build(StoreManifest manifest, string outputFolder, string filesFolder, string emlPath, DeliveryReport report)
    {
      var html = LoadEmailHtml(manifest, outputFolder, report);
      var hasEmailInfo = manifest?.OriginalRecipientInfo != null || html != null;

      if (!hasEmailInfo && report.DeliveredPaths.Count == 0)
      {
        report.DeliveryMode = FilesDeliveryMode.None;
        return;
      }

      var attachInEml = report.DeliveredPaths.Count > 0 && report.TotalSize <= _maxAttachmentsTotalSize;
      var streams = new List<Stream>();

      try
      {
        var message = CreateEnvelope(manifest);

        MimeEntity body = html != null
          ? new TextPart("html") { Text = html }
          : new TextPart("plain") { Text = string.Empty };

        if (attachInEml)
        {
          // Les pieces jointes sont lues en flux depuis le disque pendant l'ecriture : pas de
          // chargement en memoire, ce qui permet des envois de plusieurs centaines de Mo.
          var mixed = new Multipart("mixed") { body };
          foreach (var path in report.DeliveredPaths)
          {
            var stream = File.OpenRead(path);
            streams.Add(stream);
            mixed.Add(CreateAttachment(Path.GetFileName(path), stream));
          }
          message.Body = mixed;
        }
        else
        {
          message.Body = body;
        }

        message.WriteTo(emlPath);
        report.EmlPath = emlPath;
      }
      catch (Exception ex)
      {
        report.EmlFailed = true;
        report.AddError("Eml", ex);
        FileSystemCleanup.TryDeleteFile(emlPath);
        report.DeliveryMode = report.DeliveredPaths.Count > 0 ? FilesDeliveryMode.Separate : FilesDeliveryMode.None;
        return;
      }
      finally
      {
        foreach (var s in streams)
        {
          s.Dispose();
        }
      }

      if (attachInEml)
      {
        // Les fichiers sont maintenant dans le .eml : les copies en clair du dossier sont supprimees
        report.DeliveryMode = FilesDeliveryMode.Eml;
        try
        {
          Directory.Delete(filesFolder, recursive: true);
        }
        catch (Exception ex)
        {
          report.AddError("Eml: files folder could not be removed after attaching", ex);
        }
      }
      else
      {
        report.DeliveryMode = report.DeliveredPaths.Count > 0 ? FilesDeliveryMode.Separate : FilesDeliveryMode.None;
      }
    }

    /// <summary>
    /// Cree le message avec expediteur, sujet, date et destinataires tires du manifest.
    /// Les destinataires SESAR sont places en Cci (Bcc), jamais en A (To) : le .eml est une archive
    /// de l'echange et ne doit pas exposer la liste des destinataires les uns aux autres.
    /// Une adresse invalide est ignoree plutot que de faire echouer le courriel.
    /// </summary>
    private static MimeMessage CreateEnvelope(StoreManifest manifest)
    {
      var message = new MimeMessage();
      var info = manifest?.OriginalRecipientInfo;

      if (info != null)
      {
        if (TryParseMailbox(info.ContactInfo, out var from))
        {
          message.From.Add(from);
        }

        message.Subject = info.Subject ?? string.Empty;

        if (info.CreateOn != default(DateTime))
        {
          var kind = info.CreateOn.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : info.CreateOn.Kind;
          message.Date = new DateTimeOffset(DateTime.SpecifyKind(info.CreateOn, kind));
        }
      }

      if (manifest?.Recipients != null)
      {
        foreach (var r in manifest.Recipients)
        {
          if (r != null && TryParseMailbox(r.Email, out var recipient))
          {
            message.Bcc.Add(recipient);
          }
        }
      }

      return message;
    }

    /// <summary>Piece jointe MIME encodee en base64, dont le contenu est lu depuis le flux fourni.</summary>
    private static MimePart CreateAttachment(string fileName, Stream content)
    {
      return new MimePart(MimeTypes.GetMimeType(fileName))
      {
        Content = new MimeContent(content),
        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
        ContentTransferEncoding = ContentEncoding.Base64,
        FileName = fileName
      };
    }

    /// <summary>
    /// Lit le corps HTML du courriel envoye (EmailSent.html dans DirectoryPath), en dechiffrant sa
    /// version .secf si necessaire. Retourne null si aucun corps n'est disponible ; un probleme de
    /// lecture ou de dechiffrement est consigne mais n'empeche pas l'ecriture du .eml.
    /// </summary>
    private string LoadEmailHtml(StoreManifest manifest, string outputFolder, DeliveryReport report)
    {
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
          var tempFolder = Path.Combine(outputFolder, ".decrypt_" + Guid.NewGuid().ToString("N"));
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
        report.AddError($"Eml body ({EmailHtmlFileName})", ex);
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
    private static string ExtractOriginalMessage(string html)
    {
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

    /// <summary>Analyse une adresse courriel ; retourne false pour une valeur vide ou invalide.</summary>
    private static bool TryParseMailbox(string address, out MailboxAddress mailbox)
    {
      mailbox = null;
      if (string.IsNullOrWhiteSpace(address))
      {
        return false;
      }

      return MailboxAddress.TryParse(address.Trim(), out mailbox);
    }
  }
}
