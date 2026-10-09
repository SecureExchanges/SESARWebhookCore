using System.Collections.Generic;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>Courriel a envoyer par Secure Exchanges : destinataires, corps HTML et fichiers.</summary>
  public sealed class ForwardMessage
  {
    /// <summary>Adresses validees des destinataires ; chacun recoit son propre lien.</summary>
    public IReadOnlyList<string> Recipients { get; set; } = new List<string>();

    /// <summary>Sujet du courriel original.</summary>
    public string Subject { get; set; }

    /// <summary>Corps HTML du courriel original.</summary>
    public string HtmlBody { get; set; }

    /// <summary>Chemins des fichiers dechiffres, portant leur nom reel. Lus par l'envoyeur, puis effaces par le connecteur.</summary>
    public IReadOnlyList<string> FilePaths { get; set; } = new List<string>();

    /// <summary>Nom d'expediteur a afficher, ou null pour le defaut de la plateforme.</summary>
    public string SenderName { get; set; }
  }

  /// <summary>Issue d'un envoi.</summary>
  public sealed class ForwardSendResult
  {
    /// <summary>Vrai si Secure Exchanges a accepte le message.</summary>
    public bool Success { get; set; }

    /// <summary>Identifiant de suivi retourne par Secure Exchanges, ou null.</summary>
    public string TrackingId { get; set; }

    /// <summary>Identifiant du message sur la plateforme, pour le soutien ; jamais le lien de lecture.</summary>
    public string MessageId { get; set; }

    /// <summary>Detail d'echec, destine au journal serveur seulement.</summary>
    public string Error { get; set; }
  }

  /// <summary>Envoie un courriel securise. Abstrait le SDK pour permettre les tests sans reseau.</summary>
  public interface ISecureMessageSender
  {
    /// <summary>Envoie le message. Peut lever une exception en cas d'erreur de transport.</summary>
    ForwardSendResult Send(ForwardMessage message);
  }
}
