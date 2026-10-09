using Microsoft.VisualStudio.TestTools.UnitTesting;
using SecureExchangesSDK.Helpers;
using SecureExchangesSDK.Models.Messenging;
using SecureExchangesSDK.Models.Transport;
using SESARWebHook.Connectors.FileSystem;
using SESARWebHook.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SESARWebHook.Tests
{
  /// <summary>
  /// Tests du FileSystemConnector : resolution du destinataire depuis le CallBackParameter,
  /// dechiffrement du corps et des fichiers, contenu remis a l'envoyeur, nettoyage du dossier de
  /// travail. L'envoyeur Secure Exchanges est remplace par un faux : aucun appel reseau.
  /// Toutes les donnees sont fictives.
  /// </summary>
  [TestClass]
  public class FileSystemConnectorTests
  {
    private string _root;
    private string _sourceDir;
    private string _outputDir;
    private byte[] _key;
    private byte[] _iv;
    private FakeSender _sender;

    [TestInitialize]
    public void Setup()
    {
      _root = Path.Combine(Path.GetTempPath(), "SESARWebHookTests", Guid.NewGuid().ToString("N"));
      _sourceDir = Path.Combine(_root, "source");
      _outputDir = Path.Combine(_root, "output");
      Directory.CreateDirectory(_sourceDir);

      _key = RandomNumberGenerator.GetBytes(32);
      _iv = RandomNumberGenerator.GetBytes(16);
      _sender = new FakeSender();
    }

    [TestCleanup]
    public void Cleanup()
    {
      try
      {
        if (Directory.Exists(_root))
        {
          Directory.Delete(_root, recursive: true);
        }
      }
      catch
      {
        // nettoyage au mieux
      }
    }

    // ──────────────────────────────────────────────
    // Faux envoyeur
    // ──────────────────────────────────────────────

    private sealed class CapturedMessage
    {
      public ForwardMessage Message;
      /// <summary>Contenu des fichiers au moment de l'envoi, par nom de fichier.</summary>
      public Dictionary<string, byte[]> Files;
    }

    private sealed class FakeSender : ISecureMessageSender
    {
      public readonly List<CapturedMessage> Sent = new List<CapturedMessage>();
      public ForwardSendResult NextResult = new ForwardSendResult { Success = true, TrackingId = "suivi-fictif-1" };
      public Exception ThrowOnSend;
      /// <summary>Reponse par message, quand un test veut distinguer le transfert de la notification.</summary>
      public Func<ForwardMessage, ForwardSendResult> Respond;

      public ForwardSendResult Send(ForwardMessage message)
      {
        if (ThrowOnSend != null)
        {
          throw ThrowOnSend;
        }

        Sent.Add(new CapturedMessage
        {
          Message = message,
          Files = message.FilePaths.ToDictionary(p => Path.GetFileName(p), p => File.ReadAllBytes(p))
        });
        return Respond != null ? Respond(message) : NextResult;
      }
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private Dictionary<string, string> ForwardSettings(params (string key, string value)[] extra)
    {
      var settings = new Dictionary<string, string>
      {
        { "OutputPath", _outputDir },
        { "Serial", Guid.NewGuid().ToString() },
        { "ApiUser", Guid.NewGuid().ToString() },
        { "ApiPassword", Guid.NewGuid().ToString() },
        { "PrivateAESKey", Convert.ToBase64String(_key) + "_" + Convert.ToBase64String(_iv) }
      };
      foreach (var (key, value) in extra)
      {
        settings[key] = value;
      }
      return settings;
    }

    private FileSystemConnector NewConnector(params (string key, string value)[] extra)
    {
      var connector = new FileSystemConnector(_ => _sender);
      connector.Initialize(ForwardSettings(extra));
      return connector;
    }

    private static WebhookContext NewContext()
    {
      return new WebhookContext { ConnectorId = "filesystem" };
    }

    private static StoreManifest NewManifest(string callBackParameter, params (string fullPath, string realName)[] files)
    {
      return new StoreManifest
      {
        CallBackParameter = callBackParameter,
        FilesLocation = files.Select(f => new FileLocation { FullPath = f.fullPath, FileName = Path.GetFileName(f.fullPath) }).ToList(),
        FilesMetaData = files.Select(f => new FileMetaData { RealFileName = f.realName }).ToList(),
        OriginalRecipientInfo = new MessageRecipientInfo
        {
          Subject = "Sujet fictif",
          ContactInfo = "expediteur@exemple.test",
          CreateOn = new DateTime(2026, 9, 15, 10, 30, 0)
        }
      };
    }

    private string WritePlain(string name, byte[] content)
    {
      var path = Path.Combine(_sourceDir, name);
      File.WriteAllBytes(path, content);
      return path;
    }

    /// <summary>Cree name.secf a partir du contenu, sans laisser le fichier clair.</summary>
    private string WriteEncrypted(string name, byte[] content, byte[] key = null, byte[] iv = null)
    {
      var plain = WritePlain(name, content);
      CryptoHelper.EncryptSecfFile(plain, _sourceDir, key ?? _key, iv ?? _iv);
      File.Delete(plain);
      Assert.IsTrue(File.Exists(plain + ".secf"), "test setup: .secf was not produced where expected");
      return plain;
    }

    /// <summary>Reproduit la structure reelle d'un EmailSent.html SESAR : resume, premiere copie, marqueur, message original.</summary>
    private const string SesarEmailSentHtml =
      "<p style='padding:2px; margin:1px;'><strong>From</strong>: expediteur@exemple.test</p>" +
      "<p style='padding:2px; margin:1px;'><strong>To</strong>: destinataire@exemple.test</p>" +
      "<p style='padding:2px; margin:1px;'><strong>Subject</strong>: Sujet fictif</p>" +
      "<p style='padding:2px; margin:1px;'><strong>Date</strong>: 16-09-2026 14:11 GMT +00:00</p>" +
      "<body><div>PREMIERE COPIE A ECARTER</div></body>" +
      "<hr/>ORIGINAL HTML ENCODED MESSAGE BELOW<hr/><br />" +
      "<body><div class=\"elementToProof\">Message original fictif.</div></body>";

    private string WriteEmailHtml(string html, bool encrypted)
    {
      var messageDir = Path.Combine(_sourceDir, "message");
      Directory.CreateDirectory(messageDir);
      var htmlPath = Path.Combine(messageDir, "EmailSent.html");
      File.WriteAllText(htmlPath, html);
      if (encrypted)
      {
        CryptoHelper.EncryptSecfFile(htmlPath, messageDir, _key, _iv);
        File.Delete(htmlPath);
      }
      return messageDir;
    }

    /// <summary>En mode Forward, rien ne doit rester dans OutputPath : ni .eml, ni fichier, ni dossier de travail.</summary>
    private void AssertOutputClean()
    {
      if (Directory.Exists(_outputDir))
      {
        var leftovers = Directory.GetFileSystemEntries(_outputDir);
        Assert.AreEqual(0, leftovers.Length, "output folder must be empty after processing: " + string.Join(", ", leftovers.Select(Path.GetFileName)));
      }
    }

    // ──────────────────────────────────────────────
    // Initialize
    // ──────────────────────────────────────────────

    [TestMethod]
    public void Initialize_WithoutCredentials_Throws()
    {
      var connector = new FileSystemConnector(_ => _sender);
      var settings = ForwardSettings();
      settings.Remove("ApiPassword");

      Assert.ThrowsException<InvalidOperationException>(() => connector.Initialize(settings));
    }

    [TestMethod]
    public void Initialize_WithNonGuidCredential_Throws()
    {
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("Serial", "pas-un-guid")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("ApiUser", Guid.Empty.ToString())));
    }

    [TestMethod]
    public void Initialize_WithMalformedPrivateAESKey_Throws()
    {
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("PrivateAESKey", "pas-de-separateur")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("PrivateAESKey", "%%%_%%%")));
    }

    [TestMethod]
    public void Initialize_WithRemoteHttpEndpoint_Throws()
    {
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("EndPointUri", "http://www.secure-exchanges.com/_api/0217/0217.asmx")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("EndPointUri", "http://192.168.1.10/_api/0217/0217.asmx")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("EndPointUri", "pas une url")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("EndPointUri", "/_api/0217/0217.asmx")));
    }

    [TestMethod]
    public async Task Initialize_WithLocalEndpoint_IsAccepted_AndPassedToSender()
    {
      ForwardOptions captured = null;
      var connector = new FileSystemConnector(o => { captured = o; return _sender; });

      connector.Initialize(ForwardSettings(("EndPointUri", "http://localhost:5000/_api/0217/0217.asmx")));
      Assert.AreEqual(new Uri("http://localhost:5000/_api/0217/0217.asmx"), captured.EndPointUri);

      connector.Initialize(ForwardSettings(("EndPointUri", "https://localhost:44300/_api/0217/0217.asmx")));
      Assert.AreEqual(new Uri("https://localhost:44300/_api/0217/0217.asmx"), captured.EndPointUri);

      connector.Initialize(ForwardSettings(("EndPointUri", "http://127.0.0.1/_api/0217/0217.asmx")));
      Assert.IsTrue(captured.EndPointUri.IsLoopback);

      Assert.IsTrue(await connector.ValidateConfigurationAsync(ForwardSettings(("EndPointUri", "http://localhost/_api/0217/0217.asmx"))));
    }

    [TestMethod]
    public void Initialize_WithoutEndpoint_UsesPublicSecureExchangesApi()
    {
      ForwardOptions captured = null;
      new FileSystemConnector(o => { captured = o; return _sender; }).Initialize(ForwardSettings());

      Assert.AreEqual(new Uri(ForwardOptions.DefaultEndPointUri), captured.EndPointUri);
      Assert.AreEqual("https", captured.EndPointUri.Scheme);
    }

    [TestMethod]
    public void Initialize_WithOutOfRangeNumbers_Throws()
    {
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("MaximumOpenTime", "0")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("MaximumOpenTime", "100")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("MinutesExpiration", "-5")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("MaxTotalSizeBytes", "abc")));
    }

    [TestMethod]
    public async Task ValidateConfiguration_ReflectsSettings()
    {
      var connector = new FileSystemConnector(_ => _sender);

      Assert.IsTrue(await connector.ValidateConfigurationAsync(ForwardSettings()));
      var incomplete = ForwardSettings();
      incomplete.Remove("Serial");
      Assert.IsFalse(await connector.ValidateConfigurationAsync(incomplete));
      Assert.IsFalse(await connector.ValidateConfigurationAsync(new Dictionary<string, string>()));
    }

    [TestMethod]
    public async Task TestConnection_ChecksOutputPathOnly_NoSend()
    {
      var connector = NewConnector();

      Assert.IsTrue(await connector.TestConnectionAsync());
      Assert.AreEqual(0, _sender.Sent.Count, "a connection test must never send a message");
    }

    // ──────────────────────────────────────────────
    // Transfert complet
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task Forward_RawAddress_SendsOriginalBodyAndDecryptedFiles_NoEml()
    {
      var contentA = new byte[] { 1, 2, 3, 4, 5 };
      var contentB = new byte[2048];
      RandomNumberGenerator.Fill(contentB);
      var plainA = WritePlain("a.bin", contentA);
      var encB = WriteEncrypted("b.bin", contentB);

      var manifest = NewManifest("destinataire@exemple.test", (plainA, "Contrat fictif.pdf"), (encB, "Annexe fictive.xlsx"));
      manifest.DirectoryPath = WriteEmailHtml(SesarEmailSentHtml, encrypted: true);

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(1, _sender.Sent.Count);
      var sent = _sender.Sent[0];

      Assert.AreEqual("destinataire@exemple.test", sent.Message.Recipients.Single());
      Assert.AreEqual("Sujet fictif", sent.Message.Subject);
      Assert.AreEqual("expediteur@exemple.test", sent.Message.SenderName);
      StringAssert.Contains(sent.Message.HtmlBody, "Message original fictif.");
      Assert.IsFalse(sent.Message.HtmlBody.Contains("PREMIERE COPIE A ECARTER"), "rendered copy must be dropped");
      Assert.IsFalse(sent.Message.HtmlBody.Contains("<strong>From</strong>"), "summary header must be dropped");

      Assert.AreEqual(2, sent.Files.Count);
      CollectionAssert.AreEqual(contentA, sent.Files["Contrat fictif.pdf"]);
      CollectionAssert.AreEqual(contentB, sent.Files["Annexe fictive.xlsx"]);

      Assert.AreEqual("suivi-fictif-1", result.ExternalReferenceId);
      Assert.AreEqual("suivi-fictif-1", result.Metadata["TrackingId"]);
      Assert.AreEqual(true, result.Metadata["Forwarded"]);
      Assert.AreEqual(3, result.ItemsProcessed);
      Assert.AreEqual(1, result.Metadata["FilesCopied"]);
      Assert.AreEqual(1, result.Metadata["FilesDecrypted"]);
      Assert.AreEqual(true, result.Metadata["BodyIncluded"]);

      AssertOutputClean();
      Assert.AreEqual(0, Directory.GetFiles(_root, "*.eml", SearchOption.AllDirectories).Length, "no .eml must be built in Forward mode");
      Assert.IsTrue(File.Exists(plainA), "source must not be moved");
      Assert.IsTrue(File.Exists(encB + ".secf"), "encrypted source must not be moved");
      Assert.IsFalse(File.Exists(encB), "no plaintext must be left next to the source");
    }

    [TestMethod]
    public async Task Forward_DuplicateRealNames_AreBothSentWithoutOverwrite()
    {
      var s1 = WritePlain("one.bin", new byte[] { 1 });
      var s2 = WritePlain("two.bin", new byte[] { 2 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest("destinataire@exemple.test", (s1, "same.txt"), (s2, "same.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var files = _sender.Sent.Single().Files;
      CollectionAssert.AreEqual(new byte[] { 1 }, files["same.txt"]);
      CollectionAssert.AreEqual(new byte[] { 2 }, files["same (1).txt"]);
    }

    [TestMethod]
    public async Task Forward_EntryWithoutFullPath_FallsBackToDirectoryPathAndFileName()
    {
      // Cas SESAR : un fichier au contenu identique a un autre du meme envoi est laisse avec
      // FullPath null et IsDownloaded false, mais son .secf est bien present dans DirectoryPath.
      var content = new byte[] { 4, 2 };
      var first = WriteEncrypted("Doublon (1).csv", content);
      WriteEncrypted("Doublon (2).csv", content);

      var manifest = new StoreManifest
      {
        CallBackParameter = "destinataire@exemple.test",
        DirectoryPath = _sourceDir,
        FilesLocation = new List<FileLocation>
        {
          new FileLocation { FileName = "Doublon (1).csv", FullPath = first, IsDownloaded = true },
          new FileLocation { FileName = "Doublon (2).csv", FullPath = null, IsDownloaded = false }
        },
        FilesMetaData = new List<FileMetaData>
        {
          new FileMetaData { RealFileName = "Doublon (1).csv" },
          new FileMetaData { RealFileName = "Doublon (2).csv" }
        }
      };

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(2, result.Metadata["FilesDecrypted"]);
      var files = _sender.Sent.Single().Files;
      CollectionAssert.AreEqual(content, files["Doublon (1).csv"]);
      CollectionAssert.AreEqual(content, files["Doublon (2).csv"]);
    }

    [TestMethod]
    public async Task Forward_EntryWithoutFullPathNorDirectoryPath_FailsWithoutSending()
    {
      var manifest = new StoreManifest
      {
        CallBackParameter = "destinataire@exemple.test",
        FilesLocation = new List<FileLocation> { new FileLocation { FileName = "orphelin.csv" } }
      };

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(0, _sender.Sent.Count);
      Assert.AreEqual(1, result.Metadata["FilesFailed"]);
      StringAssert.Contains(result.ErrorDetails, "neither FullPath nor FileName");
    }

    [TestMethod]
    public async Task Forward_MissingRealName_FallsBackToSourceFileName()
    {
      var source = WritePlain("fallback.bin", new byte[] { 3 });
      var manifest = new StoreManifest
      {
        CallBackParameter = "destinataire@exemple.test",
        FilesLocation = new List<FileLocation> { new FileLocation { FullPath = source } },
        FilesMetaData = null
      };

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.IsTrue(_sender.Sent.Single().Files.ContainsKey("fallback.bin"));
    }

    [TestMethod]
    public async Task Forward_RecipientIsMaskedInMessageAndMetadata()
    {
      var source = WritePlain("m.bin", new byte[] { 1 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest("hubert.fictif@exemple.test", (source, "m.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.IsFalse(result.Message.Contains("hubert.fictif@"), "full address must not appear in the message");
      Assert.AreEqual("h***@exemple.test", result.Metadata["Recipient"]);
    }

    [TestMethod]
    public async Task Forward_JsonArrayParameter_UsesConfiguredKey()
    {
      var source = WritePlain("j.bin", new byte[] { 1 });
      var parameter = "[{\"Key\":\"Dossier\",\"Value\":\"12345\"},{\"Key\":\"RedirectionEmail\",\"Value\":\"destinataire@exemple.test\"}]";

      var result = await NewConnector().ProcessManifestAsync(NewManifest(parameter, (source, "j.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual("destinataire@exemple.test", _sender.Sent.Single().Message.Recipients.Single());
    }

    [TestMethod]
    public async Task Forward_JsonObjectParameter_WithCustomKey()
    {
      var source = WritePlain("o.bin", new byte[] { 1 });
      var connector = NewConnector(("RecipientParameterKey", "transfert"));

      var result = await connector.ProcessManifestAsync(NewManifest("{\"Transfert\":\"destinataire@exemple.test\"}", (source, "o.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual("destinataire@exemple.test", _sender.Sent.Single().Message.Recipients.Single());
    }

    [TestMethod]
    public async Task Forward_KeyValueParameter_IsAccepted()
    {
      var source = WritePlain("k.bin", new byte[] { 1 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest("dossier=42;RedirectionEmail=destinataire@exemple.test", (source, "k.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual("destinataire@exemple.test", _sender.Sent.Single().Message.Recipients.Single());
    }

    // ──────────────────────────────────────────────
    // Destinataire refuse : rien n'est envoye
    // ──────────────────────────────────────────────

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("[{\"Key\":\"Autre\",\"Value\":\"destinataire@exemple.test\"}]")]
    [DataRow("[]")]
    [DataRow("{\"Dossier\":\"42\"}")]
    [DataRow("{\"RedirectionEmail\":\"\"}")]
    [DataRow("dossier=42")]
    [DataRow("RedirectionEmail=")]
    public async Task Forward_NoRedirectionRequested_SucceedsWithoutSending(string parameter)
    {
      var source = WritePlain("none.bin", new byte[] { 1 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest(parameter, (source, "none.txt")), NewContext());

      Assert.IsTrue(result.Success, "expected success for parameter: " + parameter + " / " + result.ErrorDetails);
      Assert.AreEqual(0, _sender.Sent.Count, "nothing must be sent");
      Assert.AreEqual(false, result.Metadata["Forwarded"]);
      Assert.IsNull(result.ExternalReferenceId);
      AssertOutputClean();
    }

    [DataTestMethod]
    [DataRow("pas une adresse")]
    [DataRow("Nom <destinataire@exemple.test>")]
    [DataRow("a@exemple.test;b@exemple.test")]
    [DataRow("{\"RedirectionEmail\":\"pas une adresse\"}")]
    [DataRow("[{\"Key\":\"RedirectionEmail\",\"Value\":42}]")]
    [DataRow("RedirectionEmail=pas une adresse")]
    [DataRow("[pas du json")]
    [DataRow("\"une chaine json\"")]
    public async Task Forward_InvalidRecipient_FailsWithoutSending(string parameter)
    {
      var source = WritePlain("inv.bin", new byte[] { 1 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest(parameter, (source, "inv.txt")), NewContext());

      Assert.IsFalse(result.Success, "expected failure for parameter: " + parameter);
      Assert.AreEqual(0, _sender.Sent.Count, "nothing must be sent");
      Assert.IsFalse(string.IsNullOrEmpty(result.ErrorDetails));
      AssertOutputClean();
    }

    [DataTestMethod]
    [DataRow("test@testeur200.com")]
    [DataRow("x@Exemple.Test")]
    [DataRow("prenom.nom+tag@sous.domaine.exemple.org")]
    [DataRow("x@mailinator.com")]
    public async Task Forward_AnyWellFormedDomain_IsAccepted(string address)
    {
      var source = WritePlain("dom.bin", new byte[] { 1 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest(address, (source, "dom.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(address, _sender.Sent.Single().Message.Recipients.Single());
    }

    // ──────────────────────────────────────────────
    // Contenu incomplet : rien n'est envoye
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task Forward_MissingFile_FailsWithoutSending()
    {
      var present = WritePlain("ok.bin", new byte[] { 1 });
      var missing = Path.Combine(_sourceDir, "absent.bin");

      var result = await NewConnector().ProcessManifestAsync(NewManifest("destinataire@exemple.test", (present, "ok.txt"), (missing, "absent.pdf")), NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(0, _sender.Sent.Count, "an incomplete email must never be sent");
      Assert.AreEqual(1, result.Metadata["FilesFailed"]);
      Assert.AreEqual(1, result.ItemsFailed);
      AssertOutputClean();
    }

    [TestMethod]
    public async Task Forward_WrongKey_FailsWithoutSendingOrLeavingPlaintext()
    {
      var enc = WriteEncrypted("wk.bin", new byte[] { 9, 9, 9 }, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));

      var result = await NewConnector().ProcessManifestAsync(NewManifest("destinataire@exemple.test", (enc, "secret.txt")), NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(0, _sender.Sent.Count);
      Assert.AreEqual(0, Directory.GetFiles(_root, "secret.txt", SearchOption.AllDirectories).Length);
      AssertOutputClean();
    }

    [TestMethod]
    public async Task Forward_EncryptedBodyWithoutKey_FailsWithoutSending()
    {
      var manifest = NewManifest("destinataire@exemple.test");
      manifest.DirectoryPath = WriteEmailHtml("<body><p>Corps chiffre fictif.</p></body>", encrypted: true);

      var connector = new FileSystemConnector(_ => _sender);
      var settings = ForwardSettings();
      settings.Remove("PrivateAESKey");
      connector.Initialize(settings);

      var result = await connector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(0, _sender.Sent.Count);
      StringAssert.Contains(result.ErrorDetails, "PrivateAESKey");
      AssertOutputClean();
    }

    [TestMethod]
    public async Task Forward_TotalSizeOverLimit_FailsWithoutSending()
    {
      var s1 = WritePlain("big1.bin", new byte[60]);
      var s2 = WritePlain("big2.bin", new byte[60]);
      var connector = NewConnector(("MaxTotalSizeBytes", "100"));

      var result = await connector.ProcessManifestAsync(NewManifest("destinataire@exemple.test", (s1, "big1.bin"), (s2, "big2.bin")), NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(0, _sender.Sent.Count);
      Assert.AreEqual(120L, result.Metadata["FilesTotalSize"]);
      AssertOutputClean();
    }

    // ──────────────────────────────────────────────
    // Echec de l'envoi
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task Forward_SenderRefuses_ReturnsFailureAndCleansUp()
    {
      var source = WritePlain("ref.bin", new byte[] { 1 });
      _sender.NextResult = new ForwardSendResult { Success = false, Error = "status 401: identifiants fictifs refuses" };

      var result = await NewConnector().ProcessManifestAsync(NewManifest("destinataire@exemple.test", (source, "ref.txt")), NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(1, result.ItemsFailed);
      StringAssert.Contains(result.ErrorDetails, "401");
      Assert.IsNull(result.ExternalReferenceId);
      AssertOutputClean();
    }

    [TestMethod]
    public async Task Forward_SenderThrows_ReturnsFailureAndCleansUp()
    {
      var source = WritePlain("thr.bin", new byte[] { 1 });
      _sender.ThrowOnSend = new System.Net.Http.HttpRequestException("panne fictive", new System.Security.Authentication.AuthenticationException("certificat fictif refuse"));

      var result = await NewConnector().ProcessManifestAsync(NewManifest("destinataire@exemple.test", (source, "thr.txt")), NewContext());

      Assert.IsFalse(result.Success);
      StringAssert.Contains(result.ErrorDetails, "panne fictive");
      StringAssert.Contains(result.ErrorDetails, "certificat fictif refuse", "inner exception must be logged");
      AssertOutputClean();
    }

    // ──────────────────────────────────────────────
    // Cas limites du contenu
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task Forward_NoEmailInfoAndNoFiles_SendsNothingButSucceeds()
    {
      var manifest = new StoreManifest { CallBackParameter = "destinataire@exemple.test" };

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.AreEqual(0, _sender.Sent.Count);
      AssertOutputClean();
    }

    [TestMethod]
    public async Task Forward_FilesWithoutBody_SendsPlaceholderBody()
    {
      var source = WritePlain("nb.bin", new byte[] { 1 });
      var manifest = NewManifest("destinataire@exemple.test", (source, "nb.txt"));
      manifest.OriginalRecipientInfo = null;

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var sent = _sender.Sent.Single().Message;
      Assert.IsFalse(string.IsNullOrEmpty(sent.HtmlBody), "SDK requires a body");
      Assert.IsFalse(string.IsNullOrEmpty(sent.Subject), "a subject is always provided");
      Assert.IsNull(sent.SenderName);
      Assert.AreEqual(false, result.Metadata["BodyIncluded"]);
    }

    [TestMethod]
    public async Task Forward_SubjectFallsBackToBase64Subject()
    {
      var source = WritePlain("b64.bin", new byte[] { 1 });
      var manifest = NewManifest("destinataire@exemple.test", (source, "b64.txt"));
      manifest.OriginalRecipientInfo = new MessageRecipientInfo { ContactInfo = "expediteur@exemple.test" };
      manifest.Base64Subject = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Sujet encodé fictif"));

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual("Sujet encodé fictif", _sender.Sent.Single().Message.Subject);
    }

    [TestMethod]
    public async Task Forward_ConfiguredSenderName_OverridesOriginalSender()
    {
      var source = WritePlain("sn.bin", new byte[] { 1 });
      var connector = NewConnector(("SenderName", "Archives fictives"));

      var result = await connector.ProcessManifestAsync(NewManifest("destinataire@exemple.test", (source, "sn.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.AreEqual("Archives fictives", _sender.Sent.Single().Message.SenderName);
    }

    [TestMethod]
    public async Task Forward_PlainEmailHtml_EncodedOriginalIsDecoded()
    {
      var manifest = NewManifest("destinataire@exemple.test");
      manifest.DirectoryPath = WriteEmailHtml(
        "<p><strong>From</strong>: expediteur@exemple.test</p><body>copie</body>" +
        "<hr/>ORIGINAL HTML ENCODED MESSAGE BELOW<hr/><br />&lt;p&gt;Message encod&eacute; fictif&lt;/p&gt;",
        encrypted: false);

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual("<p>Message encodé fictif</p>", _sender.Sent.Single().Message.HtmlBody);
    }

    [TestMethod]
    public async Task Forward_RealNameWithPathTraversal_StaysInsideWorkFolder()
    {
      var source = WritePlain("trav.bin", new byte[] { 7 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest("destinataire@exemple.test", (source, @"..\..\..\evil.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.IsTrue(_sender.Sent.Single().Files.ContainsKey("evil.txt"));
      Assert.IsFalse(File.Exists(Path.Combine(_root, "evil.txt")));
      Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "evil.txt")));
      AssertOutputClean();
    }

    // ──────────────────────────────────────────────
    // Suppression du dossier d'echange SESAR apres l'envoi
    // ──────────────────────────────────────────────

    /// <summary>
    /// Reproduit un dossier d'echange SESAR : fichiers .secf, EmailSent.html.secf et manifest.json
    /// dans un meme dossier, qui est le DirectoryPath du manifest.
    /// </summary>
    private StoreManifest NewExchangeFolder(out string exchangeDir, params (string name, byte[] content)[] files)
    {
      exchangeDir = Path.Combine(_sourceDir, "received", "boite@exemple.test", "2026-09-22", "Sujet fictif - 2026-09-22T141814");
      Directory.CreateDirectory(exchangeDir);

      var locations = new List<FileLocation>();
      var metaData = new List<FileMetaData>();
      foreach (var (name, content) in files)
      {
        var plain = Path.Combine(exchangeDir, name);
        File.WriteAllBytes(plain, content);
        CryptoHelper.EncryptSecfFile(plain, exchangeDir, _key, _iv);
        File.Delete(plain);
        locations.Add(new FileLocation { FullPath = plain, FileName = name, IsDownloaded = true });
        metaData.Add(new FileMetaData { RealFileName = name });
      }

      var htmlPath = Path.Combine(exchangeDir, "EmailSent.html");
      File.WriteAllText(htmlPath, SesarEmailSentHtml);
      CryptoHelper.EncryptSecfFile(htmlPath, exchangeDir, _key, _iv);
      File.Delete(htmlPath);
      File.WriteAllText(Path.Combine(exchangeDir, "manifest.json"), "{ \"fictif\": true }");

      return new StoreManifest
      {
        CallBackParameter = "test@testeur200.com",
        DirectoryPath = exchangeDir,
        FilesLocation = locations,
        FilesMetaData = metaData,
        OriginalRecipientInfo = new MessageRecipientInfo { Subject = "Sujet fictif", ContactInfo = "expediteur@exemple.test", CreateOn = new DateTime(2026, 9, 22, 14, 18, 14) }
      };
    }

    [TestMethod]
    public async Task Forward_Success_DeletesSesarExchangeFolderIncludingManifest()
    {
      var manifest = NewExchangeFolder(out var exchangeDir, ("Contrat fictif.pdf", new byte[] { 1, 2, 3 }), ("Annexe.xlsx", new byte[512]));

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(2, _sender.Sent.Single().Files.Count, "files must have been sent before deletion");
      Assert.IsFalse(Directory.Exists(exchangeDir), "exchange folder (manifest included) must be deleted");
      Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(exchangeDir)), "parent day folder must be kept");
      Assert.AreEqual(true, result.Metadata["SourceDeleted"]);
      Assert.IsFalse(result.Message.Contains("warning"), result.Message);
      AssertOutputClean();
    }

    [TestMethod]
    public async Task Forward_DeleteDisabled_KeepsExchangeFolder()
    {
      var manifest = NewExchangeFolder(out var exchangeDir, ("a.pdf", new byte[] { 1 }));
      var connector = NewConnector(("DeleteSourceAfterForward", "false"));

      var result = await connector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.IsTrue(Directory.Exists(exchangeDir));
      Assert.IsTrue(File.Exists(Path.Combine(exchangeDir, "manifest.json")));
      Assert.AreEqual(false, result.Metadata["SourceDeleted"]);
    }

    [TestMethod]
    public async Task Forward_SendRefused_KeepsExchangeFolder()
    {
      var manifest = NewExchangeFolder(out var exchangeDir, ("a.pdf", new byte[] { 1 }));
      _sender.NextResult = new ForwardSendResult { Success = false, Error = "refus fictif" };

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsFalse(result.Success);
      Assert.IsTrue(Directory.Exists(exchangeDir), "nothing may be deleted when the email did not go out");
      Assert.IsTrue(File.Exists(Path.Combine(exchangeDir, "a.pdf.secf")));
    }

    [TestMethod]
    public async Task Forward_FileOutsideExchangeFolder_RefusesDeletionButSucceeds()
    {
      // Le fichier est ailleurs que dans DirectoryPath : le dossier n'est pas a nous, on ne touche a rien
      var outside = WritePlain("ailleurs.bin", new byte[] { 1 });
      var manifest = NewManifest("test@testeur200.com", (outside, "ailleurs.txt"));
      manifest.DirectoryPath = WriteEmailHtml(SesarEmailSentHtml, encrypted: false);

      var result = await NewConnector().ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.AreEqual(1, _sender.Sent.Count);
      Assert.IsTrue(Directory.Exists(manifest.DirectoryPath));
      Assert.IsTrue(File.Exists(outside));
      Assert.AreEqual(false, result.Metadata["SourceDeleted"]);
      StringAssert.Contains(result.Message, "warning");
    }

    [TestMethod]
    public async Task Forward_SesarStoreRoot_LimitsWhatCanBeDeleted()
    {
      var elsewhere = Path.Combine(_root, "autre-magasin");
      Directory.CreateDirectory(elsewhere);

      var manifest = NewExchangeFolder(out var exchangeDir, ("a.pdf", new byte[] { 1 }));
      var refused = await NewConnector(("SesarStoreRoot", elsewhere)).ProcessManifestAsync(manifest, NewContext());
      Assert.IsTrue(refused.Success, refused.Message);
      Assert.IsTrue(Directory.Exists(exchangeDir), "folder outside SesarStoreRoot must be kept");
      Assert.AreEqual(false, refused.Metadata["SourceDeleted"]);

      var accepted = await NewConnector(("SesarStoreRoot", Path.Combine(_sourceDir, "received"))).ProcessManifestAsync(manifest, NewContext());
      Assert.IsTrue(accepted.Success, accepted.Message);
      Assert.IsFalse(Directory.Exists(exchangeDir), "folder under SesarStoreRoot must be deleted");
      Assert.AreEqual(true, accepted.Metadata["SourceDeleted"]);
    }

    [TestMethod]
    public async Task Forward_NoDirectoryPath_NothingDeletedButSucceeds()
    {
      var source = WritePlain("nd.bin", new byte[] { 1 });

      var result = await NewConnector().ProcessManifestAsync(NewManifest("test@testeur200.com", (source, "nd.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.IsTrue(File.Exists(source));
      Assert.AreEqual(false, result.Metadata["SourceDeleted"]);
    }

    [TestMethod]
    public void Initialize_WithRelativeSesarStoreRoot_Throws()
    {
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("SesarStoreRoot", "SESAR\\Store")));
    }

    // ──────────────────────────────────────────────
    // Notification apres transfert
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task Forward_Success_NotifiesConfiguredAddresses()
    {
      var manifest = NewExchangeFolder(out _, ("Contrat & fictif.pdf", new byte[] { 1 }));
      manifest.OriginalRecipientInfo.Subject = "Sujet <script>fictif</script>";
      var connector = NewConnector(("NotificationEmails", "archives@exemple.test; direction@exemple.test, archives@exemple.test"));

      var result = await connector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(2, _sender.Sent.Count, "forward then notification");
      var forward = _sender.Sent[0].Message;
      var notification = _sender.Sent[1].Message;

      CollectionAssert.AreEqual(new[] { "test@testeur200.com" }, forward.Recipients.ToList());
      CollectionAssert.AreEquivalent(new[] { "archives@exemple.test", "direction@exemple.test" }, notification.Recipients.ToList());
      Assert.AreEqual(0, notification.FilePaths.Count, "notification carries no file");
      Assert.AreEqual("Notification de redirection d'enveloppe", notification.Subject);
      Assert.IsFalse(notification.Subject.Contains("script"), "original subject stays in the body, not in the notification subject");
      StringAssert.Contains(notification.HtmlBody, "test@testeur200.com");
      StringAssert.Contains(notification.HtmlBody, "expediteur@exemple.test");
      StringAssert.Contains(notification.HtmlBody, "suivi-fictif-1");
      StringAssert.Contains(notification.HtmlBody, "Contrat &amp; fictif.pdf", "file names must be html-encoded");
      StringAssert.Contains(notification.HtmlBody, "&lt;script&gt;", "subject must be html-encoded");
      Assert.IsFalse(notification.HtmlBody.Contains("<script>"), "raw tags from the manifest must never reach the notification");
      StringAssert.Contains(notification.HtmlBody, "2026-09-22 10:18", "CreateOn is UTC on the server and must be shown in Eastern time (EDT in September)");
      Assert.IsFalse(notification.HtmlBody.Contains("14:18"), "the raw UTC time must not be shown");
      Assert.AreEqual(true, result.Metadata["NotificationSent"]);
    }

    [TestMethod]
    public async Task Forward_NotificationRefused_DoesNotFailTheWebhook()
    {
      var manifest = NewExchangeFolder(out var exchangeDir, ("a.pdf", new byte[] { 1 }));
      _sender.Respond = m => m.Recipients.Contains("archives@exemple.test")
        ? new ForwardSendResult { Success = false, Error = "notification refusee fictive" }
        : new ForwardSendResult { Success = true, TrackingId = "suivi-fictif-2" };
      var connector = NewConnector(("NotificationEmails", "archives@exemple.test"));

      var result = await connector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, "the forwarded email went out, the webhook must not fail");
      Assert.AreEqual("suivi-fictif-2", result.ExternalReferenceId);
      Assert.AreEqual(false, result.Metadata["NotificationSent"]);
      StringAssert.Contains(result.Message, "notification refusee fictive");
      Assert.IsFalse(Directory.Exists(exchangeDir), "deletion happens regardless of the notification");
    }

    [TestMethod]
    public async Task Forward_SendRefused_SendsNoNotification()
    {
      var manifest = NewExchangeFolder(out _, ("a.pdf", new byte[] { 1 }));
      _sender.NextResult = new ForwardSendResult { Success = false, Error = "refus fictif" };
      var connector = NewConnector(("NotificationEmails", "archives@exemple.test"));

      var result = await connector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(1, _sender.Sent.Count, "no notification without a successful forward");
    }

    [TestMethod]
    public void Initialize_WithInvalidNotificationEmail_Throws()
    {
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("NotificationEmails", "archives@exemple.test, pas une adresse")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("NotificationEmails", "Nom <a@exemple.test>")));
    }
  }
}
