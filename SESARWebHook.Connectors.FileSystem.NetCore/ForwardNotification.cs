using SecureExchangesSDK.Models.Messenging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Construit le courriel de notification envoye, par Secure Exchanges, a la liste
  /// NotificationEmails apres un transfert reussi. Toutes les valeurs venant du manifest sont
  /// encodees HTML avant insertion : le sujet ou un nom de fichier ne peuvent injecter de balise.
  /// </summary>
  internal static class ForwardNotification
  {
    /// <summary>Sujet fixe de la notification ; le sujet original figure dans le corps.</summary>
    public const string Subject = "Notification de redirection d'enveloppe";

    public static ForwardMessage Build(
      StoreManifest manifest,
      string forwardedTo,
      IReadOnlyList<string> filePaths,
      string trackingId,
      bool sourceDeleted,
      ForwardOptions options)
    {
      var info = manifest?.OriginalRecipientInfo;
      var subject = string.IsNullOrWhiteSpace(info?.Subject) ? "(sans objet)" : info.Subject.Trim();
      var fileNames = (filePaths ?? new List<string>()).Select(Path.GetFileName).ToList();

      var body = new StringBuilder();
      body.Append("<p>Un echange SESAR a ete transfere par le webhook.</p>");
      body.Append("<table>");
      AppendRow(body, "Sujet", subject);
      AppendRow(body, "Expediteur original", info?.ContactInfo);
      AppendRow(body, "Envoye le", info != null && info.CreateOn != default(DateTime) ? FormatLocalDate(info.CreateOn) : null);
      AppendRow(body, "Transfere a", forwardedTo);
      AppendRow(body, "Fichiers", fileNames.Count == 0 ? "aucun" : fileNames.Count + " : " + string.Join(", ", fileNames));
      AppendRow(body, "Identifiant de suivi", trackingId);
      AppendRow(body, "Dossier SESAR supprime", sourceDeleted ? "oui" : "non");
      body.Append("</table>");

      return new ForwardMessage
      {
        Recipients = options.NotificationEmails.ToList(),
        Subject = Subject,
        HtmlBody = body.ToString(),
        FilePaths = new List<string>(),
        SenderName = string.IsNullOrWhiteSpace(options.SenderName) ? null : options.SenderName
      };
    }

    /// <summary>
    /// Fuseau d'affichage de la date d'envoi. Le serveur Secure Exchanges stocke CreateOn en UTC ;
    /// la notification est lue au Quebec, donc on convertit vers l'heure de l'Est plutot que
    /// d'afficher l'heure UTC ou celle, arbitraire, du serveur qui heberge le webhook.
    /// </summary>
    private const string DisplayTimeZoneWindowsId = "Eastern Standard Time";
    private const string DisplayTimeZoneIanaId = "America/Toronto";

    private static readonly Lazy<TimeZoneInfo> DisplayTimeZone = new Lazy<TimeZoneInfo>(ResolveDisplayTimeZone);

    private static TimeZoneInfo ResolveDisplayTimeZone()
    {
      foreach (var id in new[] { DisplayTimeZoneWindowsId, DisplayTimeZoneIanaId })
      {
        try
        {
          return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }
      }
      return TimeZoneInfo.Local;
    }

    /// <summary>
    /// Formate une date CreateOn pour le courriel. La valeur est traitee comme UTC quel que soit
    /// son Kind apres desérialisation (Unspecified en pratique), car c'est ainsi que le serveur l'emet.
    /// </summary>
    internal static string FormatLocalDate(DateTime createOn)
    {
      var utc = createOn.Kind == DateTimeKind.Local
        ? createOn.ToUniversalTime()
        : DateTime.SpecifyKind(createOn, DateTimeKind.Utc);

      var local = TimeZoneInfo.ConvertTimeFromUtc(utc, DisplayTimeZone.Value);
      return local.ToString("yyyy-MM-dd HH:mm");
    }

    private static void AppendRow(StringBuilder body, string label, string value)
    {
      body.Append("<tr><td><strong>").Append(WebUtility.HtmlEncode(label)).Append("</strong></td><td>")
          .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(value) ? "-" : value))
          .Append("</td></tr>");
    }
  }
}
