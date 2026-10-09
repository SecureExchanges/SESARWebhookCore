using SecureExchangesSDK.Helpers;
using SecureExchangesSDK.Models.Args;
using SecureExchangesSDK.Models.Entity;
using SecureExchangesSDK.SecureExchanges;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Envoi par le SDK Secure Exchanges (MessageHelper.MultiRecipientMessage) : un seul destinataire,
  /// notification par courriel, message envoye par la plateforme (SEMS), fichiers lus depuis le
  /// disque par le SDK, qui les chiffre et les televerse par morceaux.
  /// </summary>
  public sealed class SecureExchangesMessageSender : ISecureMessageSender
  {
    /// <summary>Code de statut retourne par l'API quand l'envoi a reussi.</summary>
    private const int StatusOk = 200;

    private readonly ForwardOptions _options;

    public SecureExchangesMessageSender(ForwardOptions options)
    {
      _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ForwardSendResult Send(ForwardMessage message)
    {
      if (message == null)
      {
        throw new ArgumentNullException(nameof(message));
      }

      if (message.Recipients == null || message.Recipients.Count == 0)
      {
        throw new ArgumentException("message has no recipient", nameof(message));
      }

      var recipients = message.Recipients.Select(email => new RecipientInfo { Email = email }).ToList();

      var args = new MutliRecipientArgs(
        _options.EndPointUri,
        _options.Serial,
        _options.ApiUser,
        _options.ApiPassword,
        recipients,
        message.HtmlBody ?? string.Empty,
        message.Subject ?? string.Empty,
        password: null,
        filesList: null,
        sendMode: SendMethodEnum.onlyEmail,
        getBackHtml: false,
        showSubject: _options.ShowSubject,
        getNotify: _options.GetNotify,
        cultureID: _options.CultureId,
        maximumOpenTime: _options.MaximumOpenTime,
        minutesExpiration: _options.MinutesExpiration);

      // Le constructeur laisse MailID vide ; le SDK s'en sert pour regrouper les televersements.
      if (args.MailID == Guid.Empty)
      {
        args.MailID = Guid.NewGuid();
      }

      if (message.FilePaths != null && message.FilePaths.Count > 0)
      {
        args.FilesPath = new List<string>(message.FilePaths);
      }

      if (!string.IsNullOrWhiteSpace(message.SenderName))
      {
        args.Name = message.SenderName;
      }

      var answer = MessageHelper.MultiRecipientMessage(args);

      if (answer == null)
      {
        return new ForwardSendResult { Success = false, Error = "MultiRecipientMessage returned no answer" };
      }

      if (answer.Status != StatusOk)
      {
        return new ForwardSendResult
        {
          Success = false,
          Error = $"Secure Exchanges answered status {answer.Status}: {answer.Data}"
        };
      }

      // Un statut 200 global ne suffit pas : chaque destinataire doit avoir recu sa propre reponse
      // avec un identifiant de message. Sans cela, rien ne prouve que la plateforme a cree l'envoi,
      // et il ne faut ni notifier ni supprimer le dossier SESAR.
      var answers = answer.RecipientsAnswer ?? new List<RecipientAnswer>();
      var incomplete = answers.Where(a => a?.Answer == null
                                          || (string.IsNullOrEmpty(a.Answer.Guid) && string.IsNullOrEmpty(a.Answer.MsgID) && a.Answer.TrackingID == Guid.Empty))
                              .ToList();

      if (answers.Count < recipients.Count || incomplete.Count > 0)
      {
        return new ForwardSendResult
        {
          Success = false,
          Error = $"Secure Exchanges answered 200 but returned {answers.Count} recipient answer(s) for {recipients.Count} recipient(s), {incomplete.Count} without message id: {answer.Data}"
        };
      }

      var first = answers[0].Answer;
      return new ForwardSendResult
      {
        Success = true,
        TrackingId = first.TrackingID != Guid.Empty ? first.TrackingID.ToString() : null,
        MessageId = !string.IsNullOrEmpty(first.MsgID) ? first.MsgID : first.Guid
      };
    }
  }
}
