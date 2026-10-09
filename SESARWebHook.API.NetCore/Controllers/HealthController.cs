using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SESARWebHook.API.Controllers
{
  [ApiController]
  [Route("api/health")]
  public class HealthController : ControllerBase
  {
    private readonly StartupConfig _config;
    private readonly IConfiguration _appConfig;

    public HealthController(StartupConfig config, IConfiguration appConfig)
    {
      _config = config;
      _appConfig = appConfig;
    }

    [HttpGet("")]
    public IActionResult HealthCheck()
    {
      return Ok(new
      {
        Status = "Healthy",
        Timestamp = DateTime.UtcNow
      });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
      var registry = _config.ConnectorRegistry;
      var processor = _config.WebhookProcessor;

      var connectorIds = registry.GetAvailableConnectorIds().ToList();
      var enabledConnectors = connectorIds.Where(id =>
      {
        var setting = _appConfig[$"Connector:{id}:Enabled"];
        return !string.IsNullOrEmpty(setting) && bool.TryParse(setting, out var enabled) && enabled;
      }).ToList();

      var secretsPath = _appConfig["ConnectorsSecretsPath"];
      if (string.IsNullOrEmpty(secretsPath))
      {
        secretsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "connectors.secrets.json");
      }

      // Diagnostics reduits a des booleens : le chemin du fichier de secrets, le repertoire
      // d'installation, l'identite du compte de service et le nom de la machine sont des
      // informations de reconnaissance et n'ont pas a sortir du poste. Ce point de terminaison
      // n'est pas authentifie. Le detail complet reste dans le journal au demarrage.
      var secretsDiag = new
      {
        SecretsFileFound = System.IO.File.Exists(secretsPath),
        FileEntropyConfigured = !string.IsNullOrEmpty(_appConfig["FileEntropy"]),
        DataProtectionScope = _appConfig["DataProtectionScope"] ?? "CurrentUser (default)"
      };

      return Ok(new
      {
        Status = "Healthy",
        Timestamp = DateTime.UtcNow,
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
        Configuration = new
        {
          ProcessorInitialized = processor != null,
          // InitializationError contient des messages d'exception et des chemins de fichiers :
          // on n'expose que le fait qu'une erreur existe.
          HasInitializationError = !string.IsNullOrEmpty(_config.InitializationError),
          DefaultConnector = _appConfig["DefaultConnectorId"] ?? "filesystem",
          DetailedLogging = _appConfig["EnableDetailedLogging"] ?? "false"
        },
        SecretsDiagnostics = secretsDiag,
        Connectors = new
        {
          Total = connectorIds.Count,
          Enabled = enabledConnectors.Count,
          Available = connectorIds,
          EnabledList = enabledConnectors
        }
      });
    }
  }
}
