using Microsoft.VisualStudio.TestTools.UnitTesting;
using MimeKit;
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
  /// Tests du FileSystemConnector : livraison des fichiers, dechiffrement, reconstruction du .eml
  /// et nommage. Tous les fichiers sont fictifs et crees dans un dossier temporaire propre au test.
  /// </summary>
  [TestClass]
  public class FileSystemConnectorTests
  {
    private string _root;
    private string _sourceDir;
    private string _outputDir;
    private byte[] _key;
    private byte[] _iv;

    /// <summary>Connecteur en mode "fichiers separes" (limite d'attachement a 0 octet).</summary>
    private FileSystemConnector _separateConnector;

    /// <summary>Connecteur avec la limite par defaut (2 Gio) : pieces jointes dans le .eml.</summary>
    private FileSystemConnector _emlConnector;

    [TestInitialize]
    public void Setup()
    {
      _root = Path.Combine(Path.GetTempPath(), "SESARWebHookTests", Guid.NewGuid().ToString("N"));
      _sourceDir = Path.Combine(_root, "source");
      _outputDir = Path.Combine(_root, "output");
      Directory.CreateDirectory(_sourceDir);
      Directory.CreateDirectory(_outputDir);

      _key = RandomNumberGenerator.GetBytes(32);
      _iv = RandomNumberGenerator.GetBytes(16);

      _separateConnector = NewConnector(("EmlMaxTotalSizeBytes", "0"));
      _emlConnector = NewConnector();
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
    // Helpers
    // ──────────────────────────────────────────────

    private FileSystemConnector NewConnector(params (string key, string value)[] extra)
    {
      var settings = new Dictionary<string, string>
      {
        { "OutputPath", _outputDir },
        { "CreateSubfolders", "false" },
        { "PrivateAESKey", Convert.ToBase64String(_key) + "_" + Convert.ToBase64String(_iv) }
      };
      foreach (var (key, value) in extra)
      {
        settings[key] = value;
      }

      var connector = new FileSystemConnector();
      connector.Initialize(settings);
      return connector;
    }

    private static WebhookContext NewContext()
    {
      return new WebhookContext
      {
        RequestId = Guid.NewGuid().ToString("N"),
        ReceivedAt = DateTime.UtcNow,
        ConnectorId = "filesystem"
      };
    }

    private static StoreManifest NewManifest(params (string fullPath, string realName)[] files)
    {
      return new StoreManifest
      {
        FilesLocation = files.Select(f => new FileLocation { FullPath = f.fullPath, FileName = Path.GetFileName(f.fullPath) }).ToList(),
        FilesMetaData = files.Select(f => new FileMetaData { RealFileName = f.realName }).ToList()
      };
    }

    private static MessageRecipientInfo NewInfo(string subject, DateTime? createOn = null)
    {
      return new MessageRecipientInfo
      {
        Subject = subject,
        ContactInfo = "expediteur@exemple.test",
        CreateOn = createOn ?? new DateTime(2026, 9, 15, 10, 30, 0)
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

    private static string EmlPathOf(IntegrationResult result)
    {
      var emlPath = result.Metadata["EmlPath"] as string;
      Assert.IsFalse(string.IsNullOrEmpty(emlPath), "EmlPath metadata is empty");
      Assert.IsTrue(File.Exists(emlPath), "eml file is missing");
      return emlPath;
    }

    private static string FilesFolderOf(IntegrationResult result)
    {
      var folder = result.Metadata["FilesFolder"] as string;
      Assert.IsFalse(string.IsNullOrEmpty(folder), "FilesFolder metadata is empty");
      Assert.IsTrue(Directory.Exists(folder), "files folder is missing");
      return folder;
    }

    private static MimeMessage LoadEml(IntegrationResult result)
    {
      return MimeMessage.Load(EmlPathOf(result));
    }

    private static byte[] AttachmentBytes(MimePart part)
    {
      using (var ms = new MemoryStream())
      {
        part.Content.DecodeTo(ms);
        return ms.ToArray();
      }
    }

    private void AssertNoManifestOrContextFiles()
    {
      Assert.AreEqual(0, Directory.GetFiles(_outputDir, "manifest_*", SearchOption.AllDirectories).Length, "no manifest file expected");
      Assert.AreEqual(0, Directory.GetFiles(_outputDir, "context_*", SearchOption.AllDirectories).Length, "no context file expected");
    }

    // ──────────────────────────────────────────────
    // Initialize
    // ──────────────────────────────────────────────

    [TestMethod]
    public void Initialize_WithMalformedPrivateAESKey_Throws()
    {
      var connector = new FileSystemConnector();
      Assert.ThrowsException<InvalidOperationException>(() =>
        connector.Initialize(new Dictionary<string, string>
        {
          { "OutputPath", _outputDir },
          { "PrivateAESKey", "pas-de-separateur" }
        }));
    }

    [TestMethod]
    public void Initialize_WithInvalidBase64PrivateAESKey_Throws()
    {
      var connector = new FileSystemConnector();
      Assert.ThrowsException<InvalidOperationException>(() =>
        connector.Initialize(new Dictionary<string, string>
        {
          { "OutputPath", _outputDir },
          { "PrivateAESKey", "%%%_%%%" }
        }));
    }

    [TestMethod]
    public void Initialize_WithInvalidEmlMaxTotalSizeBytes_Throws()
    {
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("EmlMaxTotalSizeBytes", "beaucoup")));
      Assert.ThrowsException<InvalidOperationException>(() => NewConnector(("EmlMaxTotalSizeBytes", "-1")));
    }

    // ──────────────────────────────────────────────
    // Sortie : seulement le .eml (et le dossier de fichiers au besoin)
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessManifest_EmptyManifest_WritesNothing()
    {
      var result = await _emlConnector.ProcessManifestAsync(new StoreManifest(), NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.AreEqual("none", result.Metadata["FilesDeliveryMode"]);
      Assert.IsNull(result.Metadata["EmlPath"]);
      Assert.IsNull(result.Metadata["FilesFolder"]);
      Assert.AreEqual(0, Directory.GetFileSystemEntries(_outputDir).Length, "output folder must stay empty");
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_OnlyTheEmlIsWritten()
    {
      var source = WritePlain("a.bin", new byte[] { 1 });
      var manifest = NewManifest((source, "a.txt"));
      manifest.OriginalRecipientInfo = NewInfo("Seul le eml");

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var entries = Directory.GetFileSystemEntries(_outputDir);
      Assert.AreEqual(1, entries.Length, "exactly one entry expected: " + string.Join(", ", entries.Select(Path.GetFileName)));
      Assert.AreEqual(EmlPathOf(result), Path.GetFullPath(entries[0]));
      AssertNoManifestOrContextFiles();
    }

    [TestMethod]
    public async Task ProcessManifest_Separate_FolderHasSameNameAsEml()
    {
      var s1 = WritePlain("p1.bin", new byte[60]);
      var s2 = WritePlain("p2.bin", new byte[60]);
      var manifest = NewManifest((s1, "p1.bin"), (s2, "p2.bin"));
      manifest.OriginalRecipientInfo = NewInfo("Gros envoi fictif");
      var connector = NewConnector(("EmlMaxTotalSizeBytes", "100"));

      var result = await connector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual("separate", result.Metadata["FilesDeliveryMode"]);
      Assert.AreEqual(120L, result.Metadata["FilesTotalSize"]);

      var emlPath = EmlPathOf(result);
      var folder = FilesFolderOf(result);
      Assert.AreEqual("Gros envoi fictif - 2026-09-15 10-30.eml", Path.GetFileName(emlPath));
      Assert.AreEqual(Path.GetFileNameWithoutExtension(emlPath), Path.GetFileName(folder), "folder must carry the eml name without extension");
      Assert.AreEqual(Path.GetDirectoryName(emlPath), Path.GetDirectoryName(folder), "folder must sit next to the eml");
      Assert.IsTrue(File.Exists(Path.Combine(folder, "p1.bin")));
      Assert.IsTrue(File.Exists(Path.Combine(folder, "p2.bin")));
      Assert.AreEqual(0, LoadEml(result).Attachments.Count(), "no attachment expected over the size limit");
      Assert.AreEqual(2, Directory.GetFileSystemEntries(_outputDir).Length, "only the eml and its folder expected");
      AssertNoManifestOrContextFiles();
    }

    // ──────────────────────────────────────────────
    // Livraison des fichiers (mode separe pour observer les fichiers)
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessManifest_Separate_PlaintextFile_IsCopiedWithRealName()
    {
      var content = new byte[] { 1, 2, 3, 4, 5 };
      var source = WritePlain("a1b2c3.bin", content);

      var result = await _separateConnector.ProcessManifestAsync(NewManifest((source, "Contrat fictif.pdf")), NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.AreEqual(1, result.Metadata["FilesCopied"]);
      Assert.AreEqual(0, result.Metadata["FilesDecrypted"]);
      Assert.AreEqual("separate", result.Metadata["FilesDeliveryMode"]);

      var delivered = Path.Combine(FilesFolderOf(result), "Contrat fictif.pdf");
      Assert.IsTrue(File.Exists(delivered), "copied file is missing");
      CollectionAssert.AreEqual(content, File.ReadAllBytes(delivered));
      Assert.IsTrue(File.Exists(source), "source must not be moved");
    }

    [TestMethod]
    public async Task ProcessManifest_Separate_EncryptedFile_IsDecryptedWithRealName()
    {
      var content = new byte[4096];
      RandomNumberGenerator.Fill(content);
      var plain = WriteEncrypted("d4e5f6.bin", content);

      var result = await _separateConnector.ProcessManifestAsync(NewManifest((plain, "Etats financiers fictifs.xlsx")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(0, result.Metadata["FilesCopied"]);
      Assert.AreEqual(1, result.Metadata["FilesDecrypted"]);

      var folder = FilesFolderOf(result);
      var delivered = Path.Combine(folder, "Etats financiers fictifs.xlsx");
      Assert.IsTrue(File.Exists(delivered), "decrypted file is missing");
      CollectionAssert.AreEqual(content, File.ReadAllBytes(delivered));
      Assert.AreEqual(0, Directory.GetDirectories(folder).Length, "temporary decrypt folder was not removed");
    }

    [TestMethod]
    public async Task ProcessManifest_EncryptedFileWithWrongKey_FailsWithoutLeavingPlaintext()
    {
      var plain = WriteEncrypted("g7h8i9.bin", new byte[] { 9, 9, 9 }, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));

      var result = await _separateConnector.ProcessManifestAsync(NewManifest((plain, "secret.txt")), NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(1, result.ItemsFailed);
      Assert.AreEqual(1, result.Metadata["FilesFailed"]);
      Assert.AreEqual(0, Directory.GetFiles(_outputDir, "secret.txt", SearchOption.AllDirectories).Length);
      Assert.AreEqual(0, Directory.GetDirectories(_outputDir, ".decrypt_*", SearchOption.AllDirectories).Length, "temporary decrypt folder was not removed");
      Assert.AreEqual(0, Directory.GetFileSystemEntries(_outputDir).Length, "an empty files folder must not be left behind");
    }

    [TestMethod]
    public async Task ProcessManifest_EncryptedFileWithoutKey_FailsGracefully()
    {
      var plain = WriteEncrypted("nokey.bin", new byte[] { 1 });

      var connector = new FileSystemConnector();
      connector.Initialize(new Dictionary<string, string>
      {
        { "OutputPath", _outputDir },
        { "CreateSubfolders", "false" }
      });

      var result = await connector.ProcessManifestAsync(NewManifest((plain, "x.txt")), NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(1, result.Metadata["FilesFailed"]);
      StringAssert.Contains(result.ErrorDetails, "PrivateAESKey");
    }

    [TestMethod]
    public async Task ProcessManifest_MissingFile_ReportsFailure()
    {
      var missing = Path.Combine(_sourceDir, "absent.bin");
      var manifest = NewManifest((missing, "absent.pdf"));
      manifest.OriginalRecipientInfo = NewInfo("Fichier manquant");

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(1, result.ItemsFailed);
      Assert.AreEqual(1, result.Metadata["FilesFailed"]);
      Assert.IsFalse(string.IsNullOrEmpty(result.ErrorDetails));
      Assert.IsTrue(File.Exists(EmlPathOf(result)), "the eml with the body must still be written");
      Assert.AreEqual(0, LoadEml(result).Attachments.Count());
    }

    [TestMethod]
    public async Task ProcessManifest_Separate_MixedFiles_ReportsEachOutcome()
    {
      var plainCopy = WritePlain("copy.bin", new byte[] { 1 });
      var toEncrypt = WriteEncrypted("enc.bin", new byte[] { 2 });
      var missing = Path.Combine(_sourceDir, "missing.bin");

      var result = await _separateConnector.ProcessManifestAsync(
        NewManifest((plainCopy, "copy.txt"), (toEncrypt, "enc.txt"), (missing, "missing.txt")),
        NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(3, result.Metadata["FilesTotal"]);
      Assert.AreEqual(1, result.Metadata["FilesCopied"]);
      Assert.AreEqual(1, result.Metadata["FilesDecrypted"]);
      Assert.AreEqual(1, result.Metadata["FilesFailed"]);
      var folder = FilesFolderOf(result);
      Assert.IsTrue(File.Exists(Path.Combine(folder, "copy.txt")));
      Assert.IsTrue(File.Exists(Path.Combine(folder, "enc.txt")));
    }

    // ──────────────────────────────────────────────
    // Reconstruction du .eml
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessManifest_Eml_AttachesDeliveredFilesAndRemovesFolder()
    {
      var contentA = new byte[] { 1, 2, 3, 4, 5 };
      var contentB = new byte[2048];
      RandomNumberGenerator.Fill(contentB);
      var plainA = WritePlain("a.bin", contentA);
      var encB = WriteEncrypted("b.bin", contentB);

      var manifest = NewManifest((plainA, "Contrat fictif.pdf"), (encB, "Annexe fictive.xlsx"));
      manifest.OriginalRecipientInfo = NewInfo("Envoi fictif", new DateTime(2026, 9, 15, 10, 30, 0, DateTimeKind.Utc));
      manifest.Recipients = new List<RecipientManifest> { new RecipientManifest { Email = "destinataire@exemple.test" } };

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual("eml", result.Metadata["FilesDeliveryMode"]);
      Assert.AreEqual(1, result.Metadata["FilesCopied"]);
      Assert.AreEqual(1, result.Metadata["FilesDecrypted"]);
      Assert.IsNull(result.Metadata["FilesFolder"], "files folder must be removed once attached");
      Assert.AreEqual(0, Directory.GetDirectories(_outputDir).Length, "no folder should remain in output");

      var eml = LoadEml(result);
      Assert.AreEqual("Envoi fictif", eml.Subject);
      Assert.AreEqual("expediteur@exemple.test", eml.From.Mailboxes.Single().Address);
      Assert.AreEqual("destinataire@exemple.test", eml.Bcc.Mailboxes.Single().Address, "recipients must be in Bcc");
      Assert.AreEqual(0, eml.To.Count, "To must stay empty");
      Assert.AreEqual(0, eml.Cc.Count, "Cc must stay empty");

      var attachments = eml.Attachments.OfType<MimePart>().ToList();
      Assert.AreEqual(2, attachments.Count);
      CollectionAssert.AreEqual(contentA, AttachmentBytes(attachments.Single(p => p.FileName == "Contrat fictif.pdf")));
      CollectionAssert.AreEqual(contentB, AttachmentBytes(attachments.Single(p => p.FileName == "Annexe fictive.xlsx")));
    }

    /// <summary>Reproduit la structure reelle d'un EmailSent.html SESAR : resume, premiere copie, marqueur, message original.</summary>
    private const string SesarEmailSentHtml =
      "<p style='padding:2px; margin:1px;'><strong>From</strong>: expediteur@exemple.test</p>" +
      "<p style='padding:2px; margin:1px;'><strong>To</strong>: destinataire@exemple.test</p>" +
      "<p style='padding:2px; margin:1px;'><strong>Subject</strong>: Corps fictif</p>" +
      "<p style='padding:2px; margin:1px;'><strong>Date</strong>: 16-09-2026 14:11 GMT +00:00</p>" +
      "<body><div>PREMIERE COPIE A ECARTER</div><div id=\"Signature\">Signature copie</div></body>" +
      "<hr/>ORIGINAL HTML ENCODED MESSAGE BELOW<hr/><br />" +
      "<body><div class=\"elementToProof\">Message original fictif.</div><div id=\"Signature\">Signature originale</div></body>";

    [TestMethod]
    public async Task ProcessManifest_Eml_BodyIsTheOriginalMessageAfterMarker()
    {
      var messageDir = Path.Combine(_sourceDir, "message");
      Directory.CreateDirectory(messageDir);
      File.WriteAllText(Path.Combine(messageDir, "EmailSent.html"), SesarEmailSentHtml);

      var manifest = new StoreManifest { DirectoryPath = messageDir, OriginalRecipientInfo = NewInfo("Corps fictif") };

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var body = LoadEml(result).HtmlBody;
      StringAssert.StartsWith(body, "<body>", "body must start at the original message");
      StringAssert.Contains(body, "Message original fictif.");
      StringAssert.Contains(body, "Signature originale");
      Assert.IsFalse(body.Contains("PREMIERE COPIE A ECARTER"), "first copy must be dropped");
      Assert.IsFalse(body.Contains("<strong>From</strong>"), "summary header block must be dropped");
      Assert.IsFalse(body.Contains("ORIGINAL HTML ENCODED MESSAGE BELOW"), "marker must be dropped");
      Assert.IsFalse(body.Contains("<hr"), "marker rules must be dropped");
    }

    /// <summary>Bloc resume From/To/Subject/Date tel que SESAR le place en tete du HTML.</summary>
    private const string SesarSummaryHeader =
      "<p style='padding:2px; margin:1px;'><strong>From</strong>: expediteur@exemple.test</p>" +
      "<p style='padding:2px; margin:1px;'><strong>To</strong>: a@exemple.test,b@exemple.test</p>" +
      "<p style='padding:2px; margin:1px;'><strong>Subject</strong>: asdasd</p>" +
      "<p style='padding:2px; margin:1px;'><strong>Date</strong>: 15-09-2026 20:41 GMT +00:00</p>";

    private async Task<string> HtmlBodyFor(string emailSentHtml)
    {
      var messageDir = Path.Combine(_sourceDir, "message");
      Directory.CreateDirectory(messageDir);
      File.WriteAllText(Path.Combine(messageDir, "EmailSent.html"), emailSentHtml);

      var manifest = new StoreManifest { DirectoryPath = messageDir, OriginalRecipientInfo = NewInfo("Corps") };
      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      return LoadEml(result).HtmlBody;
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_EncodedOriginalAfterMarker_IsDecoded()
    {
      // Cas observe : le message original est encode en entites et s'affiche avec ses balises visibles
      var body = await HtmlBodyFor(SesarSummaryHeader + "<body>asdasd</body>" +
        "<hr/>ORIGINAL HTML ENCODED MESSAGE BELOW<hr/><br />&lt;p&gt;Message encod&eacute; fictif &amp;amp; test&lt;/p&gt;");

      StringAssert.Contains(body, "<p>Message encodé fictif &amp; test</p>");
      Assert.IsFalse(body.Contains("&lt;"), "entities must be decoded into real tags");
      Assert.IsFalse(body.Contains("<strong>From</strong>"), "summary header block must be dropped");
      Assert.IsFalse(body.Contains("ORIGINAL HTML ENCODED MESSAGE BELOW"));
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_RawOriginalWithAmpersands_IsNotDecoded()
    {
      var body = await HtmlBodyFor(SesarSummaryHeader + "<body>copie</body>" +
        "<hr/>ORIGINAL HTML ENCODED MESSAGE BELOW<hr/><br /><body><p>Tom &amp; Jerry &lt;3</p></body>");

      StringAssert.Contains(body, "<p>Tom &amp; Jerry &lt;3</p>", "raw HTML must be kept byte for byte");
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_MarkerWithNothingAfter_FallsBackToRenderedCopy()
    {
      var body = await HtmlBodyFor(SesarSummaryHeader + "<body><p>Copie rendue fictive.</p></body>" +
        "<hr/>ORIGINAL HTML ENCODED MESSAGE BELOW<hr/><br />   ");

      StringAssert.StartsWith(body, "<body>");
      StringAssert.Contains(body, "Copie rendue fictive.");
      Assert.IsFalse(body.Contains("<strong>From</strong>"), "summary header block must be dropped");
      Assert.IsFalse(body.Contains("ORIGINAL HTML ENCODED MESSAGE BELOW"));
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_HtmlWithoutMarker_KeepsMessageWithoutSummaryHeader()
    {
      var body = await HtmlBodyFor(SesarSummaryHeader + "<body><p>Sans marqueur, le message est garde.</p></body>");

      StringAssert.StartsWith(body, "<body>");
      StringAssert.Contains(body, "Sans marqueur, le message est garde.");
      Assert.IsFalse(body.Contains("<strong>From</strong>"), "summary header block must be dropped");
      Assert.IsFalse(body.Contains("<strong>Date</strong>"), "summary header block must be dropped");
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_HtmlWithoutMarkerNorHeader_IsKeptWhole()
    {
      var body = await HtmlBodyFor("<body><p>Sans marqueur, tout est garde.</p></body>");

      Assert.AreEqual("<body><p>Sans marqueur, tout est garde.</p></body>", body.Trim());
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_BodyComesFromEncryptedEmailSentHtml()
    {
      var messageDir = Path.Combine(_sourceDir, "message");
      Directory.CreateDirectory(messageDir);
      var htmlPath = Path.Combine(messageDir, "EmailSent.html");
      File.WriteAllText(htmlPath, "<html><body><p>Corps chiffre fictif.</p></body></html>");
      CryptoHelper.EncryptSecfFile(htmlPath, messageDir, _key, _iv);
      File.Delete(htmlPath);
      Assert.IsTrue(File.Exists(htmlPath + ".secf"));

      var manifest = new StoreManifest { DirectoryPath = messageDir, OriginalRecipientInfo = NewInfo("Corps chiffre") };

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      StringAssert.Contains(LoadEml(result).HtmlBody, "Corps chiffre fictif");
      Assert.IsFalse(File.Exists(htmlPath), "plaintext html must not be left next to the source");
      Assert.AreEqual(0, Directory.GetDirectories(_outputDir, ".decrypt_*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_InvalidAddressesAreSkippedNotFatal()
    {
      var source = WritePlain("x.bin", new byte[] { 1 });
      var manifest = NewManifest((source, "x.txt"));
      manifest.OriginalRecipientInfo = new MessageRecipientInfo { Subject = "Adresse invalide", ContactInfo = "pas une adresse" };
      manifest.Recipients = new List<RecipientManifest> { new RecipientManifest { Email = "<<>>" } };

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var eml = LoadEml(result);
      Assert.AreEqual(0, eml.From.Count);
      Assert.AreEqual(0, eml.Bcc.Count);
      Assert.AreEqual(1, eml.Attachments.Count());
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_TotalExactlyAtLimit_IsAttached()
    {
      var source = WritePlain("limit.bin", new byte[100]);
      var connector = NewConnector(("EmlMaxTotalSizeBytes", "100"));

      var result = await connector.ProcessManifestAsync(NewManifest((source, "limit.bin")), NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.AreEqual("eml", result.Metadata["FilesDeliveryMode"]);
      Assert.AreEqual(1, LoadEml(result).Attachments.Count());
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_FilesWithoutEmailInfo_StillProducesEml()
    {
      var source = WritePlain("only.bin", new byte[] { 1 });

      var result = await _emlConnector.ProcessManifestAsync(NewManifest((source, "only.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var name = Path.GetFileName(EmlPathOf(result));
      StringAssert.StartsWith(name, "message - ");
      Assert.AreEqual(1, LoadEml(result).Attachments.Count());
    }

    // ──────────────────────────────────────────────
    // Nommage
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessManifest_Eml_IsNamedAfterSubjectAndSentDate()
    {
      var source = WritePlain("n.bin", new byte[] { 1 });
      var manifest = NewManifest((source, "n.txt"));
      manifest.OriginalRecipientInfo = NewInfo("Contrat: version <finale>?");

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var emlPath = EmlPathOf(result);
      Assert.AreEqual("Contrat_ version _finale__ - 2026-09-15 10-30.eml", Path.GetFileName(emlPath));
      Assert.AreEqual(Path.GetFullPath(_outputDir), Path.GetFullPath(Path.GetDirectoryName(emlPath)));
    }

    [TestMethod]
    public async Task ProcessManifest_SameSubjectAndDateTwice_DoesNotOverwrite()
    {
      var source = WritePlain("d.bin", new byte[] { 1 });
      StoreManifest Build()
      {
        var m = NewManifest((source, "d.txt"));
        m.OriginalRecipientInfo = NewInfo("Doublon", new DateTime(2026, 1, 2, 3, 4, 5));
        return m;
      }

      var r1 = await _separateConnector.ProcessManifestAsync(Build(), NewContext());
      var r2 = await _separateConnector.ProcessManifestAsync(Build(), NewContext());

      Assert.IsTrue(r1.Success && r2.Success);
      Assert.AreEqual("Doublon - 2026-01-02 03-04.eml", Path.GetFileName(EmlPathOf(r1)));
      Assert.AreEqual("Doublon - 2026-01-02 03-04 (1).eml", Path.GetFileName(EmlPathOf(r2)));
      Assert.AreEqual("Doublon - 2026-01-02 03-04", Path.GetFileName(FilesFolderOf(r1)));
      Assert.AreEqual("Doublon - 2026-01-02 03-04 (1)", Path.GetFileName(FilesFolderOf(r2)));
    }

    [TestMethod]
    public async Task ProcessManifest_Eml_WithoutSubjectOrDate_FallsBackToGenericName()
    {
      var source = WritePlain("g.bin", new byte[] { 1 });
      var manifest = NewManifest((source, "g.txt"));
      manifest.OriginalRecipientInfo = new MessageRecipientInfo { ContactInfo = "expediteur@exemple.test" };

      var result = await _emlConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var name = Path.GetFileName(EmlPathOf(result));
      StringAssert.StartsWith(name, "message - ");
      StringAssert.EndsWith(name, ".eml");
    }

    // ──────────────────────────────────────────────
    // Securite des noms de fichiers
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task ProcessManifest_RealNameWithPathTraversal_StaysInsideFilesFolder()
    {
      var source = WritePlain("trav.bin", new byte[] { 7 });

      var result = await _separateConnector.ProcessManifestAsync(NewManifest((source, @"..\..\..\evil.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      var folder = FilesFolderOf(result);
      Assert.IsTrue(File.Exists(Path.Combine(folder, "evil.txt")), "file must land inside the files folder");
      Assert.IsFalse(File.Exists(Path.Combine(_root, "evil.txt")));
      Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "evil.txt")));
    }

    [TestMethod]
    public async Task ProcessManifest_SubjectWithPathTraversal_StaysInsideOutputFolder()
    {
      var source = WritePlain("subj.bin", new byte[] { 7 });
      var manifest = NewManifest((source, "subj.txt"));
      manifest.OriginalRecipientInfo = NewInfo(@"..\..\evil");

      var result = await _separateConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(Path.GetFullPath(_outputDir), Path.GetFullPath(Path.GetDirectoryName(EmlPathOf(result))));
      Assert.AreEqual(Path.GetFullPath(_outputDir), Path.GetFullPath(Path.GetDirectoryName(FilesFolderOf(result))));
      Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(_root), "evil - 2026-09-15 10-30")));
    }

    [TestMethod]
    public async Task ProcessManifest_DuplicateRealNames_DoNotOverwrite()
    {
      var s1 = WritePlain("one.bin", new byte[] { 1 });
      var s2 = WritePlain("two.bin", new byte[] { 2 });

      var result = await _separateConnector.ProcessManifestAsync(NewManifest((s1, "same.txt"), (s2, "same.txt")), NewContext());

      Assert.IsTrue(result.Success, result.Message);
      var folder = FilesFolderOf(result);
      CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(folder, "same.txt")));
      CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(folder, "same (1).txt")));
    }

    [TestMethod]
    public async Task ProcessManifest_EntryWithoutFullPath_FallsBackToDirectoryPathAndFileName()
    {
      // Cas SESAR : un fichier au contenu identique a un autre du meme envoi est laisse avec
      // FullPath null et IsDownloaded false, mais son .secf est bien present dans DirectoryPath.
      var content = new byte[] { 4, 2 };
      var first = WriteEncrypted("Doublon (1).csv", content);
      WriteEncrypted("Doublon (2).csv", content);

      var manifest = new StoreManifest
      {
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

      var result = await _separateConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message + " / " + result.ErrorDetails);
      Assert.AreEqual(2, result.Metadata["FilesDecrypted"]);
      Assert.AreEqual(0, result.Metadata["FilesFailed"]);
      var folder = FilesFolderOf(result);
      CollectionAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(folder, "Doublon (1).csv")));
      CollectionAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(folder, "Doublon (2).csv")));
    }

    [TestMethod]
    public async Task ProcessManifest_EntryWithoutFullPathNorDirectoryPath_IsReportedAsFailed()
    {
      var manifest = new StoreManifest
      {
        FilesLocation = new List<FileLocation> { new FileLocation { FileName = "orphelin.csv" } }
      };

      var result = await _separateConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsFalse(result.Success);
      Assert.AreEqual(1, result.Metadata["FilesFailed"]);
      StringAssert.Contains(result.ErrorDetails, "neither FullPath nor FileName");
    }

    [TestMethod]
    public async Task ProcessManifest_MissingRealName_FallsBackToSourceFileName()
    {
      var source = WritePlain("fallback.bin", new byte[] { 3 });

      var manifest = new StoreManifest
      {
        FilesLocation = new List<FileLocation> { new FileLocation { FullPath = source } },
        FilesMetaData = null
      };

      var result = await _separateConnector.ProcessManifestAsync(manifest, NewContext());

      Assert.IsTrue(result.Success, result.Message);
      Assert.IsTrue(File.Exists(Path.Combine(FilesFolderOf(result), "fallback.bin")));
    }
  }
}
