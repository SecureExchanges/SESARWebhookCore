using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SecureExchangesSDK.Helpers;
using System;
using System.Linq;
using System.Net.Mail;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Tire l'adresse de transfert du CallBackParameter d'un manifest SESAR et la valide.
  ///
  /// Le CallBackParameter est une chaine libre fournie par l'expediteur au moment de l'envoi.
  /// Formes acceptees :
  ///  - l'adresse seule : "destinataire@exemple.test" ;
  ///  - un tableau JSON de paires SDK {"Key":"RedirectionEmail","Value":"destinataire@exemple.test"} ;
  ///  - un objet JSON {"RedirectionEmail":"destinataire@exemple.test"} ;
  ///  - des paires "cle=valeur" separees par ";" ou "&amp;".
  /// La cle recherchee est <see cref="ForwardOptions.RecipientParameterKey"/>.
  ///
  /// Cette valeur vient de l'exterieur : son format est valide strictement et elle n'est jamais
  /// reproduite telle quelle dans les messages d'erreur.
  /// </summary>
  internal static class ForwardRecipientResolver
  {
    private const int MaxParameterLength = 8 * 1024;

    /// <summary>
    /// Retourne false quand le manifest ne demande aucune redirection : CallBackParameter absent,
    /// ou structure (JSON, "cle=valeur") sans entree pour la cle. Le connecteur n'a alors rien a faire.
    /// Retourne true avec l'adresse validee quand une redirection est demandee.
    /// Leve InvalidOperationException, avec une raison destinee au journal serveur, quand une
    /// adresse est fournie mais invalide, ou hors des domaines permis : la demande est en erreur.
    /// </summary>
    public static bool TryResolve(string callBackParameter, ForwardOptions options, out string recipient)
    {
      return TryResolve(callBackParameter, options, out recipient, out _);
    }

    /// <summary>
    /// Variante qui explique, quand aucune redirection n'est demandee, ce que contenait le
    /// CallBackParameter : absent, ou structure avec telles cles mais pas la bonne. Seuls les noms
    /// de cles sont rapportes, jamais les valeurs. Destine au journal serveur.
    /// </summary>
    public static bool TryResolve(string callBackParameter, ForwardOptions options, out string recipient, out string absenceReason)
    {
      if (options == null)
      {
        throw new ArgumentNullException(nameof(options));
      }

      recipient = null;
      absenceReason = null;

      if (callBackParameter == null)
      {
        absenceReason = "CallBackParameter is null (SESAR sent no callback parameters)";
        return false;
      }

      if (string.IsNullOrWhiteSpace(callBackParameter))
      {
        absenceReason = "CallBackParameter is empty";
        return false;
      }

      if (callBackParameter.Length > MaxParameterLength)
      {
        throw new InvalidOperationException("CallBackParameter is too long");
      }

      var trimmed = callBackParameter.Trim();
      var candidate = ExtractCandidate(trimmed, options.RecipientParameterKey);
      if (string.IsNullOrWhiteSpace(candidate))
      {
        absenceReason = $"CallBackParameter has no '{options.RecipientParameterKey}' value; {DescribeShape(trimmed)}";
        return false;
      }

      recipient = ValidateAddress(candidate);
      return true;
    }

    /// <summary>Forme du parametre et noms des cles presentes, sans aucune valeur.</summary>
    private static string DescribeShape(string raw)
    {
      try
      {
        if (raw.StartsWith("[", StringComparison.Ordinal) || raw.StartsWith("{", StringComparison.Ordinal))
        {
          var token = JToken.Parse(raw);
          if (token is JArray array)
          {
            var keys = array.OfType<JObject>().Select(o => GetString(o, "Key")).Where(k => k != null).ToList();
            return $"JSON array of {array.Count} item(s) with keys [{string.Join(", ", keys)}]";
          }

          if (token is JObject obj)
          {
            return $"JSON object with properties [{string.Join(", ", obj.Properties().Select(p => p.Name))}]";
          }

          return "JSON of type " + token.Type;
        }

        if (raw.Contains("="))
        {
          var keys = raw.Split(new[] { ';', '&', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.IndexOf('=') > 0 ? p.Substring(0, p.IndexOf('=')).Trim() : null)
            .Where(k => !string.IsNullOrEmpty(k));
          return $"key=value pairs with keys [{string.Join(", ", keys)}]";
        }

        return $"plain text of {raw.Length} character(s)";
      }
      catch (JsonException)
      {
        return "unparseable JSON";
      }
    }

    /// <summary>"h***@domaine" : assez pour le diagnostic, sans journaliser l'adresse complete.</summary>
    public static string MaskEmail(string address)
    {
      if (string.IsNullOrEmpty(address))
      {
        return null;
      }

      var at = address.LastIndexOf('@');
      if (at <= 0)
      {
        return "***";
      }

      return address.Substring(0, 1) + "***" + address.Substring(at);
    }

    /// <summary>
    /// Isole la valeur candidate selon la forme du parametre. Retourne null quand le parametre est
    /// structure mais ne contient pas la cle : aucune redirection demandee.
    /// </summary>
    private static string ExtractCandidate(string raw, string key)
    {
      if (raw.StartsWith("[", StringComparison.Ordinal) || raw.StartsWith("{", StringComparison.Ordinal))
      {
        return ExtractFromJson(raw, key);
      }

      if (raw.Contains("="))
      {
        return ExtractFromPairs(raw, key);
      }

      return raw;
    }

    private static string ExtractFromJson(string raw, string key)
    {
      JToken token;
      try
      {
        token = JToken.Parse(raw);
      }
      catch (JsonException)
      {
        throw new InvalidOperationException("CallBackParameter looks like JSON but could not be parsed");
      }

      if (token is JArray array)
      {
        // Tableau de paires SDK : [{ "Key": "...", "Value": "..." }, ...]
        foreach (var item in array.OfType<JObject>())
        {
          var itemKey = GetString(item, "Key");
          if (itemKey != null && string.Equals(itemKey.Trim(), key, StringComparison.OrdinalIgnoreCase))
          {
            return GetString(item, "Value") ?? throw new InvalidOperationException($"CallBackParameter entry '{key}' has no string Value");
          }
        }

        return null;
      }

      if (token is JObject obj)
      {
        return GetString(obj, key);
      }

      throw new InvalidOperationException("CallBackParameter JSON must be an array of Key/Value pairs or an object");
    }

    /// <summary>Propriete d'un objet JSON, comparee sans egard a la casse ; null si absente ou non textuelle.</summary>
    private static string GetString(JObject obj, string name)
    {
      var property = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
      return property != null && property.Value.Type == JTokenType.String ? property.Value.Value<string>() : null;
    }

    private static string ExtractFromPairs(string raw, string key)
    {
      foreach (var pair in raw.Split(new[] { ';', '&', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
      {
        var separator = pair.IndexOf('=');
        if (separator <= 0)
        {
          continue;
        }

        var pairKey = pair.Substring(0, separator).Trim();
        if (string.Equals(pairKey, key, StringComparison.OrdinalIgnoreCase))
        {
          return pair.Substring(separator + 1).Trim();
        }
      }

      return null;
    }

    /// <summary>
    /// Une seule adresse nue, sans nom d'affichage ni espace, au format accepte par System.Net.Mail
    /// et par le SDK. Le domaine n'est pas filtre : n'importe quelle adresse bien formee est acceptee.
    /// </summary>
    public static string ValidateAddress(string candidate)
    {
      var address = (candidate ?? string.Empty).Trim();

      if (address.Length == 0 || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl))
      {
        throw new InvalidOperationException("recipient address is empty or malformed");
      }

      MailAddress parsed;
      try
      {
        parsed = new MailAddress(address);
      }
      catch (FormatException)
      {
        throw new InvalidOperationException("recipient address is malformed");
      }

      if (!string.Equals(parsed.Address, address, StringComparison.Ordinal))
      {
        throw new InvalidOperationException("recipient must be a bare address without display name");
      }

      if (!EmailHelper.IsValidEmailAddress(address, ignoreDisposableDomains: true, ignoreSEReservedDomains: true))
      {
        throw new InvalidOperationException("recipient address format was refused by the Secure Exchanges SDK");
      }

      return address;
    }
  }
}
