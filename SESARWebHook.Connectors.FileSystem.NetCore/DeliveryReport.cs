using System;
using System.Collections.Generic;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Bilan de la livraison des fichiers d'un manifest SESAR, alimente par
  /// <see cref="ManifestFileDeliverer"/> et par <see cref="EmailBodyReader"/>. Les connecteurs
  /// peuvent le specialiser pour y ajouter leurs propres etapes (ex. reconstruction d'un .eml).
  /// </summary>
  internal class DeliveryReport
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

    /// <summary>Chemins des fichiers livres avec succes dans le dossier de fichiers.</summary>
    public List<string> DeliveredPaths { get; } = new List<string>();

    /// <summary>Detail des echecs. Destine au journal seulement, jamais renvoye au client.</summary>
    public List<string> Errors { get; } = new List<string>();

    /// <summary>Vrai des qu'un fichier a echoue. Les specialisations ajoutent leurs propres criteres.</summary>
    public virtual bool HasFailures => Failed > 0;

    /// <summary>Consigne une erreur sous la forme "contexte: TypeException: message".</summary>
    public void AddError(string context, Exception ex)
    {
      Errors.Add($"{context}: {ex.GetType().Name}: {ex.Message}");
    }
  }
}
