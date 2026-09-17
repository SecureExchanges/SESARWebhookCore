namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>Comment les fichiers d'un echange ont ete livres.</summary>
  internal enum FilesDeliveryMode
  {
    /// <summary>Rien n'a ete ecrit : le manifest n'avait ni courriel ni fichier livre.</summary>
    None,

    /// <summary>Les fichiers sont attaches au .eml ; aucun dossier ne reste a cote.</summary>
    Eml,

    /// <summary>Les fichiers restent dans un dossier portant le nom du .eml (total au-dessus de la limite, ou .eml en echec).</summary>
    Separate
  }
}
