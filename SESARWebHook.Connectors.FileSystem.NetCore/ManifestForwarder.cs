using SecureExchangesSDK.Models.Messenging;
using SecureExchangesSDK.Models.Transport;
using SESARWebHook.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Coeur du connecteur FileSystem : renvoie, par Secure Exchanges, le courriel d un echange
  /// SESAR a l'adresse indiquee dans le CallBackParameter du manifest. Aucun .eml n'est construit.
  ///
  /// Pour chaque manifest :
  ///  1. <see cref="ForwardRecipientResolver"/> tire l'adresse du CallBackParameter et la valide ;
  ///  2. <see cref="ManifestFileDeliverer"/> dechiffre les fichiers dans un dossier de travail prive
  ///     sous OutputPath et <see cref="EmailBodyReader"/> extrait le corps HTML original ;
  ///  3. si tout a pu etre recupere, <see cref="ISecureMessageSender"/> envoie sujet, corps et
  ///     fichiers par le SDK ; sinon rien n'est envoye ;
  ///  4. apres un envoi reussi : suppression du dossier d'echange SESAR
  ///     (<see cref="SesarExchangeCleanup"/>) et notification a NotificationEmails
  ///     (<see cref="ForwardNotification"/>), tous deux en avertissement seulement s'ils echouent ;
  ///  5. le dossier de travail est toujours efface, envoi reussi ou non.
  /// </summary>
  internal sealed class ManifestForwarder
  {
    /// <summary>Corps envoye quand l'echange n'a aucun corps lisible : le SDK exige un message.</summary>
    private const string EmptyHtmlBody = "<p></p>";

    /// <summary>Sujet de repli quand le manifest n'en porte aucun.</summary>
    private const string FallbackSubject = "(sans objet)";

    private readonly ForwardOptions _options;
    private readonly ManifestFileDeliverer _deliverer;
    private readonly EmailBodyReader _bodyReader;
    private readonly ISecureMessageSender _sender;
    private readonly string _connectorId;

    public ManifestForwarder(ForwardOptions options, ManifestFileDeliverer deliverer, EmailBodyReader bodyReader, ISecureMessageSender sender, string connectorId)
    {
      _options = options ?? throw new ArgumentNullException(nameof(options));
      _deliverer = deliverer ?? throw new ArgumentNullException(nameof(deliverer));
      _bodyReader = bodyReader ?? throw new ArgumentNullException(nameof(bodyReader));
      _sender = sender ?? throw new ArgumentNullException(nameof(sender));
      _connectorId = connectorId;
    }

    /// <summary>
    /// Un manifest sans destinataire de redirection est un succes sans action : SESAR n'a rien a
    /// reessayer. Tout echec avant l'envoi (destinataire fourni mais invalide, fichier manquant,
    /// cle absente, limite de taille) empeche l'envoi : un courriel incomplet ne part jamais.
    /// Le detail va dans ErrorDetails (journal seulement, jamais renvoye au client).
    /// </summary>
    public async Task<IntegrationResult> ForwardAsync(StoreManifest manifest, string outputFolder)
    {
      string recipient;
      try
      {
        if (!ForwardRecipientResolver.TryResolve(manifest?.CallBackParameter, _options, out recipient, out var absenceReason))
        {
          return new IntegrationResult
          {
            Success = true,
            Message = $"Nothing forwarded: {absenceReason}",
            ConnectorId = _connectorId,
            Metadata = new Dictionary<string, object> { { "Recipient", null }, { "Forwarded", false } }
          };
        }
      }
      catch (InvalidOperationException ex)
      {
        return IntegrationResult.Fail("Invalid forwarding recipient in the manifest CallBackParameter; nothing was sent", ex.Message, _connectorId);
      }

      var workFolder = Path.Combine(outputFolder, ".forward_" + Guid.NewGuid().ToString("N"));

      try
      {
        Directory.CreateDirectory(workFolder);
        var filesFolder = Path.Combine(workFolder, "files");

        var report = new DeliveryReport();
        string html = null;
        await Task.Run(() =>
        {
          _deliverer.Deliver(manifest, filesFolder, report);
          html = _bodyReader.Load(manifest, workFolder, report);
        });

        var metadata = BuildMetadata(report, recipient, html != null);

        if (report.Failed > 0 || report.Errors.Count > 0)
        {
          var problem = report.Failed > 0
            ? $"{report.Failed} of {report.TotalFiles} file(s) could not be delivered"
            : "the email body could not be read";

          return new IntegrationResult
          {
            Success = false,
            Message = $"Nothing was sent: {problem}",
            ErrorDetails = string.Join(Environment.NewLine, report.Errors),
            ConnectorId = _connectorId,
            ItemsFailed = report.Failed > 0 ? report.Failed : 1,
            Metadata = metadata
          };
        }

        var info = manifest?.OriginalRecipientInfo;
        var hasEmailInfo = info != null || html != null;
        if (!hasEmailInfo && report.DeliveredPaths.Count == 0)
        {
          return new IntegrationResult
          {
            Success = true,
            Message = "Manifest contained no email information and no files; nothing forwarded",
            ConnectorId = _connectorId,
            Metadata = metadata
          };
        }

        if (report.TotalSize > _options.MaxTotalSizeBytes)
        {
          return new IntegrationResult
          {
            Success = false,
            Message = "Nothing was sent: total size of the files exceeds MaxTotalSizeBytes",
            ErrorDetails = $"total {report.TotalSize} bytes, limit {_options.MaxTotalSizeBytes} bytes",
            ConnectorId = _connectorId,
            ItemsFailed = report.DeliveredPaths.Count,
            Metadata = metadata
          };
        }

        var message = new ForwardMessage
        {
          Recipients = new List<string> { recipient },
          Subject = ResolveSubject(manifest),
          HtmlBody = html ?? EmptyHtmlBody,
          FilePaths = report.DeliveredPaths.AsReadOnly(),
          SenderName = ResolveSenderName(info)
        };

        var sendResult = await Task.Run(() => _sender.Send(message));

        if (sendResult == null || !sendResult.Success)
        {
          return new IntegrationResult
          {
            Success = false,
            Message = "Secure Exchanges did not accept the forwarded message",
            ErrorDetails = sendResult?.Error ?? "sender returned no result",
            ConnectorId = _connectorId,
            ItemsFailed = 1,
            Metadata = metadata
          };
        }

        metadata["TrackingId"] = sendResult.TrackingId;
        metadata["MessageId"] = sendResult.MessageId;
        metadata["Forwarded"] = true;

        // A partir d'ici le courriel est parti : les etapes suivantes ne font plus echouer le
        // webhook, sinon SESAR reessaierait et le destinataire recevrait un double.
        var warnings = new List<string>();

        var sourceDeleted = false;
        if (_options.DeleteSourceAfterForward)
        {
          sourceDeleted = SesarExchangeCleanup.TryDeleteExchangeFolder(manifest, _options, out var reason);
          if (!sourceDeleted)
          {
            warnings.Add("SESAR exchange folder was not deleted: " + reason);
          }
        }
        metadata["SourceDeleted"] = sourceDeleted;

        var notified = false;
        if (_options.NotificationEmails.Count > 0)
        {
          notified = TryNotify(manifest, recipient, report.DeliveredPaths, sendResult.TrackingId, sourceDeleted, warnings);
        }
        metadata["NotificationSent"] = notified;

        var summary = $"Email forwarded to {MaskEmail(recipient)} with {report.DeliveredPaths.Count} file(s), tracking {sendResult.TrackingId ?? "-"}, message {sendResult.MessageId ?? "-"}";
        if (warnings.Count > 0)
        {
          summary += "; warning: " + string.Join("; ", warnings);
        }

        return new IntegrationResult
        {
          Success = true,
          Message = summary,
          ConnectorId = _connectorId,
          ExternalReferenceId = sendResult.TrackingId,
          ItemsProcessed = 1 + report.DeliveredPaths.Count,
          Metadata = metadata
        };
      }
      catch (Exception ex)
      {
        return IntegrationResult.Fail("Failed to forward email through Secure Exchanges", DescribeException(ex), _connectorId);
      }
      finally
      {
        // Les fichiers dechiffres ne survivent jamais au traitement, envoi reussi ou non
        FileSystemCleanup.TryDeleteDirectory(workFolder);
      }
    }

    /// <summary>Envoie la notification a NotificationEmails ; un echec est consigne en avertissement, jamais en echec du webhook.</summary>
    private bool TryNotify(StoreManifest manifest, string recipient, IReadOnlyList<string> filePaths, string trackingId, bool sourceDeleted, List<string> warnings)
    {
      try
      {
        var notification = ForwardNotification.Build(manifest, recipient, filePaths, trackingId, sourceDeleted, _options);
        var answer = _sender.Send(notification);
        if (answer != null && answer.Success)
        {
          return true;
        }

        warnings.Add("notification was refused: " + (answer?.Error ?? "sender returned no result"));
        return false;
      }
      catch (Exception ex)
      {
        warnings.Add("notification failed: " + DescribeException(ex));
        return false;
      }
    }

    /// <summary>
    /// "Type: message" suivi de la chaine des exceptions internes. Une erreur TLS ou HTTP du SDK
    /// n'est comprehensible qu'avec son exception interne (certificat refuse, hote injoignable).
    /// Destine au journal serveur seulement.
    /// </summary>
    private static string DescribeException(Exception ex)
    {
      var parts = new List<string>();
      for (var current = ex; current != null; current = current.InnerException)
      {
        parts.Add($"{current.GetType().Name}: {current.Message}");
      }
      return string.Join(" <- ", parts);
    }

    private static Dictionary<string, object> BuildMetadata(DeliveryReport report, string recipient, bool bodyIncluded)
    {
      return new Dictionary<string, object>
      {
        { "Recipient", MaskEmail(recipient) },
        { "Forwarded", false },
        { "BodyIncluded", bodyIncluded },
        { "FilesTotal", report.TotalFiles },
        { "FilesTotalSize", report.TotalSize },
        { "FilesCopied", report.Copied },
        { "FilesDecrypted", report.Decrypted },
        { "FilesFailed", report.Failed }
      };
    }

    /// <summary>Sujet du manifest : OriginalRecipientInfo.Subject, sinon Base64Subject decode, sinon un repli.</summary>
    private static string ResolveSubject(StoreManifest manifest)
    {
      var subject = manifest?.OriginalRecipientInfo?.Subject;
      if (!string.IsNullOrWhiteSpace(subject))
      {
        return subject.Trim();
      }

      if (!string.IsNullOrWhiteSpace(manifest?.Base64Subject))
      {
        try
        {
          var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(manifest.Base64Subject.Trim()));
          if (!string.IsNullOrWhiteSpace(decoded))
          {
            return decoded.Trim();
          }
        }
        catch (FormatException)
        {
          // Base64Subject inexploitable : repli ci-dessous
        }
      }

      return FallbackSubject;
    }

    /// <summary>
    /// Nom d'expediteur affiche : SenderName configure, sinon l'adresse de l'expediteur original
    /// quand elle est bien formee, sinon null (defaut de la plateforme).
    /// </summary>
    private string ResolveSenderName(MessageRecipientInfo info)
    {
      if (!string.IsNullOrWhiteSpace(_options.SenderName))
      {
        return _options.SenderName;
      }

      var contact = info?.ContactInfo?.Trim();
      if (string.IsNullOrEmpty(contact))
      {
        return null;
      }

      try
      {
        return new MailAddress(contact).Address;
      }
      catch (FormatException)
      {
        return null;
      }
    }

    private static string MaskEmail(string address)
    {
      return ForwardRecipientResolver.MaskEmail(address);
    }
  }
}
