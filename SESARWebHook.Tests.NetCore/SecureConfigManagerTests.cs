using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESARWebHook.Core.Configuration;
using System;
using System.IO;
using System.Text;

namespace SESARWebHook.Tests
{
  /// <summary>
  /// Tests de lecture du fichier connectors.secrets.json.
  /// Toutes les valeurs sont fictives et le fichier est cree dans un dossier temporaire.
  ///
  /// SecureConfigManager est statique et met ses secrets en cache : le nettoyage remet la
  /// configuration sur un dossier vide, afin qu'aucun autre test ne voie de secrets.
  /// </summary>
  [TestClass]
  public class SecureConfigManagerTests
  {
    private const string FictitiousWebHookKey = "Q0xFX1dFQkhPT0tfRklDVElWRQ==";
    private const string FictitiousWebHookIv = "SVZfV0VCSE9PS19GSUNUSUY=";
    private const string FictitiousPrivateAesKey = "Q0xFX0ZJQ1RJVkU=_SVZfRklDVElG";

    private string _dir;
    private string _emptyDir;

    [TestInitialize]
    public void Setup()
    {
      var root = Path.Combine(Path.GetTempPath(), "SESARWebHookSecretsTests", Guid.NewGuid().ToString("N"));
      _dir = Path.Combine(root, "config");
      _emptyDir = Path.Combine(root, "vide");
      Directory.CreateDirectory(_dir);
      Directory.CreateDirectory(_emptyDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
      // Etat statique remis a zero : plus aucun fichier de secrets visible pour les tests suivants.
      SecureConfigManager.Initialize(null, null, null, _emptyDir);
      SecureConfigManager.ReloadSecrets();

      try
      {
        var root = Directory.GetParent(_dir).FullName;
        if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
      }
      catch
      {
        // nettoyage au mieux
      }
    }

    private string WriteSecrets(Encoding encoding)
    {
      var json = "{\n" +
                 $"  \"WebHookEncryptionKey\": \"{FictitiousWebHookKey}\",\n" +
                 $"  \"WebHookEncryptionIV\": \"{FictitiousWebHookIv}\",\n" +
                 $"  \"PrivateAESKey\": \"{FictitiousPrivateAesKey}\"\n" +
                 "}";

      var path = Path.Combine(_dir, "connectors.secrets.json");
      File.WriteAllText(path, json, encoding);
      return path;
    }

    private static void UseSecretsFile(string path, string appBasePath)
    {
      SecureConfigManager.Initialize(path, null, null, appBasePath);
      SecureConfigManager.ReloadSecrets();
    }

    /// <summary>
    /// Cas du poste client : le fichier est enregistre par un editeur Windows ou par
    /// Set-Content, qui ajoutent une marque d'ordre des octets. Les cles doivent etre lues.
    /// </summary>
    [TestMethod]
    public void Secrets_PlainTextJsonWithBom_KeysAreRead()
    {
      var path = WriteSecrets(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
      var bytes = File.ReadAllBytes(path);
      Assert.AreEqual(0xEF, bytes[0], "test setup: le fichier devait commencer par un BOM");

      UseSecretsFile(path, _dir);

      Assert.AreEqual(FictitiousWebHookKey, SecureConfigManager.GetWebHookEncryptionKey());
      Assert.AreEqual(FictitiousWebHookIv, SecureConfigManager.GetWebHookEncryptionIV());
      Assert.AreEqual(FictitiousPrivateAesKey, SecureConfigManager.GetPrivateAESKey());
    }

    [TestMethod]
    public void Secrets_PlainTextJsonWithoutBom_KeysAreRead()
    {
      var path = WriteSecrets(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

      UseSecretsFile(path, _dir);

      Assert.AreEqual(FictitiousWebHookKey, SecureConfigManager.GetWebHookEncryptionKey());
    }

    /// <summary>Le fichier en clair doit etre chiffre sur place des la premiere lecture.</summary>
    [TestMethod]
    public void Secrets_PlainTextFile_IsProtectedOnFirstRead()
    {
      var path = WriteSecrets(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

      UseSecretsFile(path, _dir);
      Assert.AreEqual(FictitiousWebHookKey, SecureConfigManager.GetWebHookEncryptionKey());

      var after = File.ReadAllBytes(path);
      Assert.AreNotEqual(0x7B, after[0], "le fichier devait etre chiffre");
      Assert.AreNotEqual(0xEF, after[0], "le fichier devait etre chiffre");

      // Relecture du fichier desormais chiffre : les memes cles doivent revenir.
      SecureConfigManager.ReloadSecrets();
      Assert.AreEqual(FictitiousWebHookKey, SecureConfigManager.GetWebHookEncryptionKey());
    }

    /// <summary>Fichier absent : aucune cle, et aucune exception.</summary>
    [TestMethod]
    public void Secrets_MissingFile_ReturnsNoKeys()
    {
      UseSecretsFile(Path.Combine(_dir, "absent.json"), _dir);

      Assert.IsNull(SecureConfigManager.GetWebHookEncryptionKey());
      Assert.IsNull(SecureConfigManager.GetPrivateAESKey());
    }
  }
}
