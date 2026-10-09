using System;
using System.Collections.Generic;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Parametres du connecteur FileSystem, valides et types une seule fois a l'initialisation.
  ///
  /// Sources : la section Connector:filesystem d'appsettings.json pour les valeurs non sensibles,
  /// connectors.secrets.json pour la PrivateAESKey (propre au connecteur, sinon globale) et les
  /// identifiants API Secure Exchanges (Serial, ApiUser, ApiPassword).
  /// </summary>
  internal sealed class FileSystemConnectorOptions
  {
    /// <summary>Dossier de travail ou les fichiers sont dechiffres le temps de l'envoi, puis effaces.</summary>
    public string OutputPath { get; private set; }

    /// <summary>Dechiffreur des .secf, construit depuis la PrivateAESKey (sans cle si aucune n'est configuree).</summary>
    public SecfFileDecryptor Decryptor { get; private set; }

    /// <summary>Options d'envoi par Secure Exchanges.</summary>
    public ForwardOptions Forward { get; private set; }

    /// <summary>Vrai quand une cle est disponible pour dechiffrer les .secf.</summary>
    public bool HasDecryptionKey => Decryptor != null && Decryptor.HasKey;

    private FileSystemConnectorOptions()
    {
    }

    /// <summary>
    /// Construit les options a partir du dictionnaire de parametres du connecteur.
    /// Une valeur absente ou mal formee (cle non base64, identifiants manquants, point d'acces
    /// invalide) leve une InvalidOperationException : mieux vaut refuser de demarrer que tourner
    /// mal configure.
    /// </summary>
    public static FileSystemConnectorOptions FromSettings(Dictionary<string, string> settings)
    {
      if (settings == null)
      {
        throw new ArgumentNullException(nameof(settings));
      }

      return new FileSystemConnectorOptions
      {
        OutputPath = Get(settings, "OutputPath") ?? @"C:\SESARWebHook\Output\",
        // Priorite a la cle propre au connecteur, repli sur la cle globale des secrets
        Decryptor = SecfFileDecryptor.FromSettings(settings),
        Forward = ForwardOptions.FromSettings(settings)
      };
    }

    /// <summary>Retourne la valeur du parametre, ou null si absent ou vide.</summary>
    private static string Get(Dictionary<string, string> settings, string name)
    {
      return settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
  }
}
