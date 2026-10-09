using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SESARWebHook.Core.Configuration
{
  public static class SecureConfigManager
  {
    private const string SecretsFileName = "connectors.secrets.json";
    private static ConnectorsSecretsConfig _cachedSecrets;
    private static readonly object _lock = new object();

    // Configuration values set from appsettings.json via Initialize()
    private static string _connectorsSecretsPath;
    private static string _dataProtectionScope;
    private static string _fileEntropy;
    private static string _appBasePath;

    public static void Initialize(string connectorsSecretsPath, string dataProtectionScope, string fileEntropy, string appBasePath)
    {
      _connectorsSecretsPath = connectorsSecretsPath;
      _dataProtectionScope = dataProtectionScope;
      _fileEntropy = fileEntropy;
      _appBasePath = appBasePath ?? AppDomain.CurrentDomain.BaseDirectory;
      _cachedSecrets = null;
    }

    public static string SecretsFilePath
    {
      get
      {
        if (!string.IsNullOrEmpty(_connectorsSecretsPath))
        {
          return _connectorsSecretsPath;
        }

        var appPath = _appBasePath ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(appPath, SecretsFileName);
      }
    }

    public static DataProtectionScope ProtectionScope
    {
      get
      {
        if (!string.IsNullOrEmpty(_dataProtectionScope) &&
            _dataProtectionScope.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase))
        {
          return DataProtectionScope.LocalMachine;
        }
        return DataProtectionScope.CurrentUser;
      }
    }

    private static byte[] GetEntropy()
    {
      if (!string.IsNullOrEmpty(_fileEntropy))
      {
        try
        {
          return Convert.FromBase64String(_fileEntropy);
        }
        catch
        {
          Trace.TraceWarning("\"FileEntropy\" value is not a valid base64 string.");
        }
      }
      return null;
    }

    #region Lecture / Écriture protégée

    public static ConnectorsSecretsConfig Secrets
    {
      get
      {
        if (_cachedSecrets != null)
          return _cachedSecrets;

        lock (_lock)
        {
          if (_cachedSecrets != null)
            return _cachedSecrets;

          _cachedSecrets = LoadAndProtectSecrets();
          return _cachedSecrets;
        }
      }
    }

    private static ConnectorsSecretsConfig LoadAndProtectSecrets()
    {
      string filePath = SecretsFilePath;

      if (!File.Exists(filePath))
      {
        System.Diagnostics.Trace.TraceWarning(
            $"[SecureConfigManager] Fichier de secrets introuvable : '{filePath}'");
        return new ConnectorsSecretsConfig();
      }

      byte[] entropy = GetEntropy();
      byte[] fileBytes = File.ReadAllBytes(filePath);

      if (IsPlainTextJson(fileBytes))
      {
        // Encoding.UTF8.GetString conserve la marque d'ordre des octets (BOM) en tete de la
        // chaine, et le desserialiseur JSON la refuse. Les editeurs Windows et PowerShell
        // ecrivent couramment un BOM : on le retire avant lecture, sans quoi un fichier de
        // secrets parfaitement valide serait rejete et les cles resteraient introuvables.
        int start = HasUtf8Bom(fileBytes) ? 3 : 0;
        string fileContent = Encoding.UTF8.GetString(fileBytes, start, fileBytes.Length - start);
        var config = JsonConvert.DeserializeObject<ConnectorsSecretsConfig>(fileContent);

        ProtectAndSave(filePath, fileContent, entropy);

        return config ?? new ConnectorsSecretsConfig();
      }
      else
      {
        try
        {
          byte[] decryptedBytes = ProtectedData.Unprotect(fileBytes, entropy, ProtectionScope);
          string json = Encoding.UTF8.GetString(decryptedBytes);
          var config = JsonConvert.DeserializeObject<ConnectorsSecretsConfig>(json);
          return config ?? new ConnectorsSecretsConfig();
        }
        catch (CryptographicException ex)
        {
          throw new InvalidOperationException(
              $"Impossible de déchiffrer le fichier de secrets '{filePath}'. " +
              $"Scope={ProtectionScope}, User={Environment.UserName}, Machine={Environment.MachineName}. " +
              "Vérifiez que le fichier a été chiffré par le même compte de service et sur la même machine. " +
              "Pour réinitialiser : supprimez le fichier chiffré et déposez une nouvelle version en clair.",
              ex);
        }
      }
    }

    /// <summary>Vrai si le contenu commence par une marque d'ordre des octets UTF-8.</summary>
    private static bool HasUtf8Bom(byte[] fileBytes)
    {
      return fileBytes != null &&
             fileBytes.Length >= 3 &&
             fileBytes[0] == 0xEF &&
             fileBytes[1] == 0xBB &&
             fileBytes[2] == 0xBF;
    }

    private static bool IsPlainTextJson(byte[] fileBytes)
    {
      if (fileBytes == null || fileBytes.Length == 0)
        return false;

      int offset = HasUtf8Bom(fileBytes) ? 3 : 0;

      while (offset < fileBytes.Length)
      {
        byte b = fileBytes[offset];
        if (b == ' ' || b == '\t' || b == '\r' || b == '\n')
        {
          offset++;
          continue;
        }
        break;
      }

      return offset < fileBytes.Length && fileBytes[offset] == (byte)'{';
    }

    private static void ProtectAndSave(string filePath, string jsonContent, byte[] entropy)
    {
      try
      {
        byte[] dataBytes = Encoding.UTF8.GetBytes(jsonContent);
        byte[] protectedBytes = ProtectedData.Protect(dataBytes, entropy, ProtectionScope);
        File.WriteAllBytes(filePath, protectedBytes);

        System.Diagnostics.Trace.TraceInformation(
            $"[SecureConfigManager] Fichier de secrets chiffré avec succès ({protectedBytes.Length} octets, Scope={ProtectionScope})");
      }
      catch (CryptographicException ex)
      {
        System.Diagnostics.Trace.TraceError(
            $"[SecureConfigManager] ERREUR DPAPI Protect : {ex.Message} | Scope={ProtectionScope} | User={Environment.UserName}");
      }
      catch (UnauthorizedAccessException ex)
      {
        System.Diagnostics.Trace.TraceError(
            $"[SecureConfigManager] ERREUR PERMISSION ÉCRITURE sur '{filePath}' : {ex.Message} | " +
            $"User={Environment.UserName} — Le compte Application Pool IIS doit avoir les droits Lecture/Écriture sur ce fichier.");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Trace.TraceError(
            $"[SecureConfigManager] ERREUR inattendue lors du chiffrement : {ex.GetType().Name}: {ex.Message}");
      }
    }

    #endregion

    #region Accès aux secrets

    public static Dictionary<string, string> GetConnectorSecrets(string connectorId)
    {
      var secrets = Secrets;
      if (secrets?.Connectors == null)
        return new Dictionary<string, string>();

      if (secrets.Connectors.TryGetValue(connectorId, out var connectorSecrets))
      {
        return connectorSecrets.Secrets ?? new Dictionary<string, string>();
      }

      return new Dictionary<string, string>();
    }

    public static string GetSecret(string connectorId, string secretKey)
    {
      var secrets = GetConnectorSecrets(connectorId);
      return secrets.TryGetValue(secretKey, out var value) ? value : null;
    }

    public static Dictionary<string, string> GetHandlerSecrets(string handlerId)
    {
      var secrets = Secrets;
      if (secrets?.Handlers == null)
        return new Dictionary<string, string>();

      if (secrets.Handlers.TryGetValue(handlerId, out var handlerSecrets))
      {
        return handlerSecrets.Secrets ?? new Dictionary<string, string>();
      }

      return new Dictionary<string, string>();
    }

    public static string GetHandlerSecret(string handlerId, string secretKey)
    {
      var secrets = GetHandlerSecrets(handlerId);
      return secrets.TryGetValue(secretKey, out var value) ? value : null;
    }

    public static string GetWebHookEncryptionKey()
    {
      return Secrets?.WebHookEncryptionKey;
    }

    public static string GetWebHookEncryptionIV()
    {
      return Secrets?.WebHookEncryptionIV;
    }

    public static string GetPrivateAESKey()
    {
      return Secrets?.PrivateAESKey;
    }

    public static string GetFileDecryptionKey()
    {
      return Secrets?.GetFileDecryptionKey();
    }

    public static string GetFileDecryptionIV()
    {
      return Secrets?.GetFileDecryptionIV();
    }

    public static void ReloadSecrets()
    {
      lock (_lock)
      {
        _cachedSecrets = null;
      }
    }

    #endregion
  }
}
