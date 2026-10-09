using System;
using System.Collections.Generic;
using System.Linq;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Parametres du mode Forward du connecteur FileSystem : identifiants et options d'envoi du SDK
  /// Secure Exchanges, regles de validation du destinataire.
  ///
  /// Sources : la section Connector:filesystem d'appsettings.json pour les valeurs non sensibles,
  /// connectors.secrets.json (Connectors:filesystem:Secrets) pour Serial, ApiUser et ApiPassword.
  /// </summary>
  public sealed class ForwardOptions
  {
    /// <summary>Point d'acces public de l'API Secure Exchanges.</summary>
    public const string DefaultEndPointUri = "https://www.secure-exchanges.com/_api/0217/0217.asmx";

    /// <summary>Culture des notifications envoyees au destinataire.</summary>
    public const string DefaultCultureId = "fr-CA";

    /// <summary>Nombre d'ouvertures permises avant destruction du message.</summary>
    public const int DefaultMaximumOpenTime = 5;

    /// <summary>Duree de vie du message : 14 jours.</summary>
    public const int DefaultMinutesExpiration = 14 * 24 * 60;

    /// <summary>Total maximal des fichiers d'un envoi : 2,5 Gio, limite documentee du SDK.</summary>
    public const long DefaultMaxTotalSizeBytes = 2_684_354_560L;

    /// <summary>Cle recherchee dans un CallBackParameter structure (JSON ou "cle=valeur").</summary>
    public const string DefaultRecipientParameterKey = "RedirectionEmail";

    /// <summary>Numero de serie de la licence Secure Exchanges.</summary>
    public Guid Serial { get; private set; }

    /// <summary>Utilisateur API Secure Exchanges.</summary>
    public Guid ApiUser { get; private set; }

    /// <summary>Mot de passe API Secure Exchanges.</summary>
    public Guid ApiPassword { get; private set; }

    /// <summary>Point d'acces de l'API, obligatoirement en HTTPS.</summary>
    public Uri EndPointUri { get; private set; }

    /// <summary>Culture des notifications (ex. fr-CA, en-CA).</summary>
    public string CultureId { get; private set; }

    /// <summary>Nombre d'ouvertures permises (1 a 99).</summary>
    public int MaximumOpenTime { get; private set; }

    /// <summary>Duree de vie du message en minutes.</summary>
    public int MinutesExpiration { get; private set; }

    /// <summary>Vrai pour que le proprietaire du compte API soit avise a l'ouverture.</summary>
    public bool GetNotify { get; private set; }

    /// <summary>Vrai pour afficher le sujet dans la notification.</summary>
    public bool ShowSubject { get; private set; }

    /// <summary>Total maximal, en octets, des fichiers d'un envoi.</summary>
    public long MaxTotalSizeBytes { get; private set; }

    /// <summary>Cle du destinataire dans un CallBackParameter structure.</summary>
    public string RecipientParameterKey { get; private set; }

    /// <summary>Nom d'expediteur affiche par Secure Exchanges ; null pour reprendre l'adresse de l'expediteur original.</summary>
    public string SenderName { get; private set; }

    /// <summary>Vrai pour supprimer le dossier d'echange SESAR (manifest.DirectoryPath, manifest inclus) apres un envoi reussi.</summary>
    public bool DeleteSourceAfterForward { get; private set; }

    /// <summary>
    /// Racine du magasin SESAR (chemin complet), ou null. Quand elle est donnee, seul un dossier
    /// d'echange situe strictement sous cette racine peut etre supprime.
    /// </summary>
    public string SesarStoreRoot { get; private set; }

    /// <summary>Adresses avisees par Secure Exchanges apres chaque transfert reussi ; vide pour aucune notification.</summary>
    public IReadOnlyList<string> NotificationEmails { get; private set; }

    private ForwardOptions()
    {
    }

    /// <summary>
    /// Construit les options a partir du dictionnaire de parametres du connecteur.
    /// Toute valeur absente ou mal formee parmi celles qui sont obligatoires leve une
    /// InvalidOperationException : mieux vaut refuser de demarrer que tourner mal configure.
    /// </summary>
    public static ForwardOptions FromSettings(Dictionary<string, string> settings)
    {
      if (settings == null)
      {
        throw new ArgumentNullException(nameof(settings));
      }

      var options = new ForwardOptions
      {
        Serial = RequireGuid(settings, "Serial"),
        ApiUser = RequireGuid(settings, "ApiUser"),
        ApiPassword = RequireGuid(settings, "ApiPassword"),
        EndPointUri = ParseEndPoint(Get(settings, "EndPointUri") ?? DefaultEndPointUri),
        CultureId = Get(settings, "CultureID") ?? DefaultCultureId,
        MaximumOpenTime = ParseInt(settings, "MaximumOpenTime", DefaultMaximumOpenTime, 1, 99),
        MinutesExpiration = ParseInt(settings, "MinutesExpiration", DefaultMinutesExpiration, 1, int.MaxValue),
        GetNotify = ParseBool(settings, "GetNotify", false),
        ShowSubject = ParseBool(settings, "ShowSubject", true),
        MaxTotalSizeBytes = ParseLong(settings, "MaxTotalSizeBytes", DefaultMaxTotalSizeBytes),
        RecipientParameterKey = Get(settings, "RecipientParameterKey") ?? DefaultRecipientParameterKey,
        SenderName = Get(settings, "SenderName"),
        DeleteSourceAfterForward = ParseBool(settings, "DeleteSourceAfterForward", true),
        SesarStoreRoot = ParseStoreRoot(Get(settings, "SesarStoreRoot")),
        NotificationEmails = ParseEmails(Get(settings, "NotificationEmails"))
      };

      return options;
    }

    /// <summary>Chemin complet et absolu de la racine du magasin SESAR, ou null si non configure.</summary>
    private static string ParseStoreRoot(string raw)
    {
      if (raw == null)
      {
        return null;
      }

      try
      {
        if (!System.IO.Path.IsPathRooted(raw))
        {
          throw new InvalidOperationException("SesarStoreRoot must be an absolute path");
        }

        return System.IO.Path.GetFullPath(raw);
      }
      catch (ArgumentException ex)
      {
        throw new InvalidOperationException("SesarStoreRoot is not a valid path", ex);
      }
    }

    /// <summary>Liste d'adresses separees par des virgules ou des points-virgules ; chacune doit etre bien formee.</summary>
    private static IReadOnlyList<string> ParseEmails(string raw)
    {
      var emails = new List<string>();
      if (raw == null)
      {
        return emails;
      }

      foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
      {
        var candidate = part.Trim();
        if (candidate.Length == 0)
        {
          continue;
        }

        try
        {
          var address = ForwardRecipientResolver.ValidateAddress(candidate);
          if (!emails.Contains(address, StringComparer.OrdinalIgnoreCase))
          {
            emails.Add(address);
          }
        }
        catch (InvalidOperationException ex)
        {
          throw new InvalidOperationException("NotificationEmails contains an invalid address: " + ex.Message, ex);
        }
      }

      return emails;
    }

    /// <summary>Retourne la valeur du parametre, ou null si absent ou vide.</summary>
    private static string Get(Dictionary<string, string> settings, string name)
    {
      return settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static Guid RequireGuid(Dictionary<string, string> settings, string name)
    {
      var raw = Get(settings, name);
      if (raw == null)
      {
        throw new InvalidOperationException($"{name} is required in Forward mode (connectors.secrets.json, Connectors:filesystem:Secrets)");
      }

      if (!Guid.TryParse(raw, out var value) || value == Guid.Empty)
      {
        throw new InvalidOperationException($"{name} must be a valid non-empty GUID");
      }

      return value;
    }

    /// <summary>
    /// L'API n'est jointe qu'en HTTPS absolu : identifiants et contenu ne circulent jamais en clair.
    /// Seule exception : HTTP vers la boucle locale (localhost, 127.0.0.1, ::1), pour un serveur
    /// Secure Exchanges de developpement sur le meme poste ; ce trafic ne quitte pas la machine.
    /// </summary>
    private static Uri ParseEndPoint(string raw)
    {
      if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
      {
        throw new InvalidOperationException("EndPointUri must be an absolute URL");
      }

      if (uri.Scheme == Uri.UriSchemeHttps)
      {
        return uri;
      }

      if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
      {
        return uri;
      }

      throw new InvalidOperationException("EndPointUri must use https (http is only accepted for a loopback address such as localhost)");
    }

    private static int ParseInt(Dictionary<string, string> settings, string name, int defaultValue, int min, int max)
    {
      var raw = Get(settings, name);
      if (raw == null)
      {
        return defaultValue;
      }

      if (!int.TryParse(raw, out var value) || value < min || value > max)
      {
        throw new InvalidOperationException($"{name} must be an integer between {min} and {max}");
      }

      return value;
    }

    private static long ParseLong(Dictionary<string, string> settings, string name, long defaultValue)
    {
      var raw = Get(settings, name);
      if (raw == null)
      {
        return defaultValue;
      }

      if (!long.TryParse(raw, out var value) || value <= 0)
      {
        throw new InvalidOperationException($"{name} must be a positive integer number of bytes");
      }

      return value;
    }

    private static bool ParseBool(Dictionary<string, string> settings, string name, bool defaultValue)
    {
      var raw = Get(settings, name);
      if (raw == null)
      {
        return defaultValue;
      }

      if (!bool.TryParse(raw, out var value))
      {
        throw new InvalidOperationException($"{name} must be 'true' or 'false'");
      }

      return value;
    }

  }
}
