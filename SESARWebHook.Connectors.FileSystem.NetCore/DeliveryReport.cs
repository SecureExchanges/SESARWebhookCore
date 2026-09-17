using System;
using System.Collections.Generic;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Bilan du traitement d'un manifest. Alimente d'abord par la livraison des fichiers
  /// (<see cref="ManifestFileDeliverer"/>), puis par la reconstruction du courriel
  /// (<see cref="EmlBuilder"/>), et enfin traduit en IntegrationResult par le connecteur.
  /// </summary>
  internal sealed class DeliveryReport
  {
    /// <summary>Nombre de fichiers references par le manifest.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Fichiers trouves en clair et copies.</summary>
    public int Copied { get; set; }

    /// <summary>Fichiers trouves en .secf et dechiffres.</summary>
    public int Decrypted { get; set; }

    /// <summary>Fichiers qui n'ont pu etre livres (introuvables, cle absente, dechiffrement en echec).</summary>
    public int Failed { get; set; }

    /// <summary>Taille totale, en octets, des fichiers livres.</summary>
    public long TotalSize { get; set; }

    /// <summary>Vrai si le .eml n'a pas pu etre ecrit.</summary>
    public bool EmlFailed { get; set; }

    /// <summary>Chemin du .eml ecrit, ou null.</summary>
    public string EmlPath { get; set; }

    /// <summary>Mode de livraison retenu pour les fichiers.</summary>
    public FilesDeliveryMode DeliveryMode { get; set; } = FilesDeliveryMode.None;

    /// <summary>Chemins des fichiers livres avec succes dans le dossier de fichiers.</summary>
    public List<string> DeliveredPaths { get; } = new List<string>();

    /// <summary>Detail des echecs. Destine au journal seulement, jamais renvoye au client.</summary>
    public List<string> Errors { get; } = new List<string>();

    /// <summary>Vrai des qu'un fichier ou le .eml a echoue.</summary>
    public bool HasFailures => Failed > 0 || EmlFailed;

    /// <summary>Consigne une erreur sous la forme "contexte: TypeException: message".</summary>
    public void AddError(string context, Exception ex)
    {
      Errors.Add($"{context}: {ex.GetType().Name}: {ex.Message}");
    }
  }
}
