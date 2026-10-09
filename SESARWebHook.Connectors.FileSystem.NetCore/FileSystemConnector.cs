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
  /// Connecteur qui renvoie, par Secure Exchanges, le courriel d'un echange SESAR au destinataire
  /// indique dans le CallBackParameter du manifest.
  ///
  /// Pour chaque manifest recu (<see cref="ManifestForwarder"/>) :
  ///  1. l'adresse de transfert est tiree du CallBackParameter et validee ;
  ///  2. le corps (EmailSent.html) et les fichiers .secf sont dechiffres avec la PrivateAESKey dans
  ///     un sous-dossier temporaire de OutputPath ;
  ///  3. sujet, corps HTML original et fichiers sont envoyes par le SDK Secure Exchanges
  ///     (MessageHelper.MultiRecipientMessage) ; si quelque chose manque, rien n'est envoye ;
  ///  4. apres un envoi reussi, le dossier d'echange SESAR (DirectoryPath, manifest inclus) est
  ///     supprime et une notification est envoyee a NotificationEmails ;
  ///  5. le sous-dossier temporaire est efface, envoi reussi ou non. Rien ne reste sur le disque.
  ///
  /// Configuration (appsettings.json, section Connector:filesystem) :
  ///   OutputPath                dossier de travail temporaire (obligatoire)
  ///   EndPointUri               point d'acces de l'API Secure Exchanges (https ; http tolere sur localhost seulement)
  ///   DeleteSourceAfterForward  "true" (defaut) pour supprimer le dossier d'echange SESAR apres l'envoi
  ///   SesarStoreRoot            racine du magasin SESAR ; seul un dossier sous cette racine peut etre supprime
  ///   CultureID, MaximumOpenTime, MinutesExpiration, GetNotify, ShowSubject, MaxTotalSizeBytes,
  ///   RecipientParameterKey, SenderName
  /// Secrets (connectors.secrets.json, Connectors:filesystem:Secrets) :
  ///   Serial, ApiUser, ApiPassword   identifiants API Secure Exchanges
  ///   NotificationEmails             adresses avisees apres chaque transfert, separees par des virgules
  ///   PrivateAESKey                  "base64key_base64iv", propre au connecteur, sinon la cle globale
  /// </summary>
  public class FileSystemConnector : IIntegrationConnector
  {
    private readonly Func<ForwardOptions, ISecureMessageSender> _senderFactory;

    private FileSystemConnectorOptions _options;
    private ManifestForwarder _forwarder;

    public string ConnectorId => "filesystem";
    public string DisplayName => "File System";
    public string Description => "Decrypts each SESAR exchange (body and files) and re-sends it through Secure Exchanges to the recipient given in the manifest CallBackParameter";
    public string Version => "3.0.0";

    public IEnumerable<string> RequiredConfigurationKeys => new[] { "OutputPath", "Serial", "ApiUser", "ApiPassword" };

    /// <summary>Connecteur de production : envoi par le SDK Secure Exchanges.</summary>
    public FileSystemConnector()
      : this(options => new SecureExchangesMessageSender(options))
    {
    }

    /// <summary>Permet de substituer l'envoyeur (tests sans reseau).</summary>
    public FileSystemConnector(Func<ForwardOptions, ISecureMessageSender> senderFactory)
    {
      _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
    }

    #region Configuration

    /// <summary>
    /// Lit et valide les parametres, puis prepare les collaborateurs. Une configuration invalide
    /// leve une InvalidOperationException : le connecteur refuse de demarrer mal configure.
    /// </summary>
    public void Initialize(Dictionary<string, string> settings)
    {
      _options = FileSystemConnectorOptions.FromSettings(settings);

      var deliverer = new ManifestFileDeliverer(_options.Decryptor);
      var bodyReader = new EmailBodyReader(_options.Decryptor);
      var sender = _senderFactory(_options.Forward) ?? throw new InvalidOperationException("sender factory returned null");
      _forwarder = new ManifestForwarder(_options.Forward, deliverer, bodyReader, sender, ConnectorId);
    }

    /// <summary>Valide l'ensemble des parametres (OutputPath, identifiants API, point d'acces) sans initialiser.</summary>
    public Task<bool> ValidateConfigurationAsync(Dictionary<string, string> settings)
    {
      if (settings == null || !settings.TryGetValue("OutputPath", out var outputPath) || string.IsNullOrWhiteSpace(outputPath))
      {
        return Task.FromResult(false);
      }

      try
      {
        FileSystemConnectorOptions.FromSettings(settings);
        return Task.FromResult(true);
      }
      catch (Exception)
      {
        return Task.FromResult(false);
      }
    }

    /// <summary>
    /// Verifie que le dossier de travail existe (ou peut etre cree) et est accessible en ecriture.
    /// Aucun appel a Secure Exchanges : un test ne doit jamais provoquer d'envoi.
    /// </summary>
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

    /// <summary>
    /// Point d'entree : transfere le courriel par Secure Exchanges. Le detail des echecs va dans
    /// ErrorDetails (journal seulement, jamais renvoye au client).
    /// </summary>
    public async Task<IntegrationResult> ProcessManifestAsync(StoreManifest manifest, WebhookContext context)
    {
      if (_options == null)
      {
        return IntegrationResult.Fail("File system connector not initialized", "Initialize was not called", ConnectorId);
      }

      try
      {
        Directory.CreateDirectory(_options.OutputPath);
      }
      catch (Exception ex)
      {
        return IntegrationResult.Fail("Failed to prepare the work folder", ex.Message, ConnectorId);
      }

      return await _forwarder.ForwardAsync(manifest, _options.OutputPath);
    }
  }
}
