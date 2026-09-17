using SecureExchangesSDK.Models.Messenging;
using SESARWebHook.Core.Interfaces;
using SESARWebHook.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SESARWebHook.Connectors.FileSystem
{
  /// <summary>
  /// Connecteur qui reconstruit sur le disque local le courriel d'un echange SESAR.
  ///
  /// Pour chaque manifest recu :
  ///  1. <see cref="ManifestFileDeliverer"/> recupere les fichiers : copie s'ils existent en clair,
  ///     sinon dechiffrement de leur version ".secf" par <see cref="SecfFileDecryptor"/> ;
  ///  2. <see cref="EmlBuilder"/> reconstruit le courriel en ".eml" : corps depuis EmailSent.html,
  ///     expediteur, destinataires, sujet et date depuis le manifest ;
  ///  3. si le total des fichiers ne depasse pas EmlMaxTotalSizeBytes, ils sont attaches au .eml ;
  ///     sinon ils restent dans un dossier portant le meme nom que le .eml, a cote.
  ///
  /// Nom produit (<see cref="OutputNaming"/>) : "&lt;sujet&gt; - &lt;date d'envoi yyyy-MM-dd HH-mm&gt;.eml".
  ///
  /// Configuration (appsettings.json, section Connector:filesystem) :
  ///   OutputPath            dossier de sortie (obligatoire)
  ///   CreateSubfolders      "true" pour un sous-dossier par jour (yyyy-MM-dd)
  ///   EmlMaxTotalSizeBytes  limite d'attachement en octets, defaut 2 Gio
  /// Secret (connectors.secrets.json) :
  ///   PrivateAESKey         "base64key_base64iv", global ou propre au connecteur
  /// </summary>
  public class FileSystemConnector : IIntegrationConnector
  {
    private FileSystemConnectorOptions _options;
    private ManifestFileDeliverer _deliverer;
    private EmlBuilder _emlBuilder;

    public string ConnectorId => "filesystem";
    public string DisplayName => "File System";
    public string Description => "Rebuilds each SESAR exchange as a .eml file (decrypted attachments included) on the local file system";
    public string Version => "2.0.0";

    public IEnumerable<string> RequiredConfigurationKeys => new[] { "OutputPath" };

    #region Configuration

    /// <summary>
    /// Lit et valide les parametres, puis prepare les collaborateurs. Une configuration invalide
    /// leve une InvalidOperationException : le connecteur refuse de demarrer mal configure.
    /// </summary>
    public void Initialize(Dictionary<string, string> settings)
    {
      _options = FileSystemConnectorOptions.FromSettings(settings);

      var decryptor = new SecfFileDecryptor(_options.Key, _options.Iv);
      _deliverer = new ManifestFileDeliverer(decryptor);
      _emlBuilder = new EmlBuilder(decryptor, _options.EmlMaxTotalSizeBytes);
    }

    /// <summary>Valide la presence d'un OutputPath non vide.</summary>
    public Task<bool> ValidateConfigurationAsync(Dictionary<string, string> settings)
    {
      var valid = settings != null
                  && settings.ContainsKey("OutputPath")
                  && !string.IsNullOrWhiteSpace(settings["OutputPath"]);
      return Task.FromResult(valid);
    }

    /// <summary>Verifie que le dossier de sortie existe (ou peut etre cree) et est accessible en ecriture.</summary>
    public Task<bool> TestConnectionAsync()
    {
      if (_options == null)
      {
        return Task.FromResult(false);
      }

      try
      {
        Directory.CreateDirectory(_options.OutputPath);

        var testFile = Path.Combine(_options.OutputPath, ".write_test");
        File.WriteAllText(testFile, "test");
        File.Delete(testFile);

        return Task.FromResult(true);
      }
      catch
      {
        return Task.FromResult(false);
      }
    }

    #endregion

    #region Traitement du manifest

    /// <summary>
    /// Point d'entree : livre les fichiers, reconstruit le .eml et rend compte du resultat.
    /// Un fichier en echec n'interrompt pas les autres ; le resultat global est alors en echec
    /// avec le detail dans ErrorDetails (journal seulement, jamais renvoye au client).
    /// </summary>
    public async Task<IntegrationResult> ProcessManifestAsync(StoreManifest manifest, WebhookContext context)
    {
      if (_options == null)
      {
        return IntegrationResult.Fail("File system connector not initialized", "Initialize was not called", ConnectorId);
      }

      try
      {
        var outputFolder = PrepareOutputFolder();

        // Nom de base commun au .eml et au dossier de fichiers, unique dans le dossier de sortie
        var baseName = OutputNaming.MakeUniqueBaseName(outputFolder, OutputNaming.BuildBaseName(manifest));
        var emlPath = SafePaths.GetSafeDestinationPath(outputFolder, baseName + OutputNaming.EmlExtension);
        var filesFolder = SafePaths.GetSafeDestinationPath(outputFolder, baseName);

        var report = await Task.Run(() =>
        {
          var r = _deliverer.Deliver(manifest, filesFolder);
          _emlBuilder.Build(manifest, outputFolder, filesFolder, emlPath, r);
          FileSystemCleanup.RemoveFolderIfEmpty(filesFolder);
          return r;
        });

        return BuildResult(report, filesFolder);
      }
      catch (Exception ex)
      {
        return IntegrationResult.Fail("Failed to write email to file system", ex.Message, ConnectorId);
      }
    }

    /// <summary>Cree le dossier de sortie, avec un sous-dossier par jour si CreateSubfolders est actif.</summary>
    private string PrepareOutputFolder()
    {
      var outputFolder = _options.OutputPath;
      if (_options.CreateSubfolders)
      {
        outputFolder = Path.Combine(_options.OutputPath, DateTime.Now.ToString("yyyy-MM-dd"));
      }

      Directory.CreateDirectory(outputFolder);
      return outputFolder;
    }

    /// <summary>Traduit le rapport interne en IntegrationResult, avec les compteurs en metadonnees.</summary>
    private IntegrationResult BuildResult(DeliveryReport report, string filesFolder)
    {
      var folderKept = Directory.Exists(filesFolder);

      var metadata = new Dictionary<string, object>
      {
        { "EmlPath", report.EmlPath },
        { "FilesFolder", folderKept ? filesFolder : null },
        { "FilesDeliveryMode", report.DeliveryMode.ToString().ToLowerInvariant() },
        { "FilesTotal", report.TotalFiles },
        { "FilesTotalSize", report.TotalSize },
        { "FilesCopied", report.Copied },
        { "FilesDecrypted", report.Decrypted },
        { "FilesFailed", report.Failed }
      };

      var itemsProcessed = (report.EmlPath != null ? 1 : 0) + report.Copied + report.Decrypted;
      var reference = report.EmlPath ?? (folderKept ? filesFolder : null);

      if (report.HasFailures)
      {
        var problem = report.Failed > 0
          ? $"{report.Failed} of {report.TotalFiles} file(s) could not be delivered"
          : "the .eml could not be built";

        return new IntegrationResult
        {
          Success = false,
          Message = report.EmlPath != null ? $"Email written to {report.EmlPath} but {problem}" : $"Processing failed: {problem}",
          ErrorDetails = string.Join(Environment.NewLine, report.Errors),
          ConnectorId = ConnectorId,
          ExternalReferenceId = reference,
          ItemsProcessed = itemsProcessed,
          ItemsFailed = report.Failed + (report.EmlFailed ? 1 : 0),
          Metadata = metadata
        };
      }

      string message;
      switch (report.DeliveryMode)
      {
        case FilesDeliveryMode.Eml:
          message = $"Email rebuilt to {report.EmlPath} with {report.TotalFiles} attachment(s)";
          break;
        case FilesDeliveryMode.Separate:
          message = $"Email rebuilt to {report.EmlPath}, {report.TotalFiles} file(s) kept in {filesFolder} (total size over limit)";
          break;
        default:
          message = report.EmlPath != null
            ? $"Email rebuilt to {report.EmlPath}"
            : "Manifest contained no email information and no files; nothing written";
          break;
      }

      return new IntegrationResult
      {
        Success = true,
        Message = message,
        ConnectorId = ConnectorId,
        ExternalReferenceId = reference,
        ItemsProcessed = itemsProcessed,
        Metadata = metadata
      };
    }

    #endregion
  }
}
