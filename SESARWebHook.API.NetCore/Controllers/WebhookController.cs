using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SecureExchangesSDK.Models.Transport;
using SESARWebHook.Core.Models;
using SESARWebHook.Core.Services;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace SESARWebHook.API.Controllers
{
  public class SesarWebHookRequest
  {
    [JsonProperty("args")]
    public SesarWebHook Args { get; set; }
  }

  [ApiController]
  [Route("api/webhook")]
  public class WebhookController : ControllerBase
  {
    /// <summary>
    /// Message unique renvoye pour tout echec de traitement.
    ///
    /// Volontairement non specifique : distinguer "dechiffrement echoue" de "fichier introuvable"
    /// fournirait a un appelant non authentifie un oracle sur le contenu du payload et sur le
    /// systeme de fichiers. Le detail reel est journalise cote serveur.
    /// </summary>
    private const string GenericFailureMessage = "Le traitement de la requete a echoue.";

    private readonly StartupConfig _config;
    private readonly IConfiguration _appConfig;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(StartupConfig config, IConfiguration appConfig, ILogger<WebhookController> logger)
    {
      _config = config;
      _appConfig = appConfig;
      _logger = logger;
    }

    [HttpPost("")]
    public async Task<IActionResult> ProcessWebhook([FromBody] SesarWebHookRequest request)
    {
      if (request?.Args == null)
      {
        return BadRequest("Invalid webhook data. Expected format: { \"args\": { \"HashKey\": \"...\", \"CryptedObject\": \"...\" } }");
      }

      var defaultConnector = _appConfig["DefaultConnectorId"] ?? "filesystem";
      return await ProcessWithConnector(request.Args, defaultConnector);
    }

    [HttpPost("handler/{handlerId}")]
    public async Task<IActionResult> ProcessWebhookWithHandler(
        string handlerId,
        [FromBody] SesarWebHookRequest request)
    {
      if (request?.Args == null)
      {
        return BadRequest("Invalid webhook data. Expected format: { \"args\": { \"HashKey\": \"...\", \"CryptedObject\": \"...\" } }");
      }

      if (string.IsNullOrEmpty(handlerId))
      {
        return BadRequest("Handler ID is required");
      }

      var processor = _config.WebhookProcessor;
      if (processor == null)
      {
        return StatusCode(500, new { Error = "Webhook processor not initialized. Check encryption key configuration." });
      }

      var handlerRegistry = _config.HandlerRegistry;
      if (handlerRegistry == null || !handlerRegistry.HandlerExists(handlerId))
      {
        return NotFound();
      }

      var genericConnector = _config.GenericConnector;
      if (genericConnector == null)
      {
        return StatusCode(500, new { Error = "Generic connector not initialized." });
      }

      if (!processor.ValidateAuthentication(request.Args.HashKey))
      {
        return StatusCode((int)HttpStatusCode.Unauthorized, IntegrationResult.Fail(
            "Authentication failed", "Invalid hash key", handlerId));
      }

      string jsonPayload;
      try
      {
        jsonPayload = processor.DecryptPayload(request.Args.CryptedObject);
      }
      catch (System.Exception ex)
      {
        return StatusCode(500, IntegrationResult.Fail(
            "Decryption failed", ex.Message, handlerId));
      }

      SecureExchangesSDK.Models.Messenging.StoreManifest manifest;
      try
      {
        manifest = SecureExchangesSDK.Helpers.SerializationHelper.DeserializeFromJson<SecureExchangesSDK.Models.Messenging.StoreManifest>(jsonPayload);
      }
      catch (System.Exception ex)
      {
        return StatusCode(500, IntegrationResult.Fail(
            "Deserialization failed", ex.Message, handlerId));
      }

      var context = new WebhookContext
      {
        ConnectorId = handlerId,
        Metadata = new Dictionary<string, object>
                {
                    { "HandlerId", handlerId }
                },
        RawPayload = jsonPayload
      };

      var result = await genericConnector.ProcessManifestAsync(manifest, context);

      if (result.Success)
      {
        return Ok(result);
      }
      else
      {
        return StatusCode(500, result);
      }
    }

    [HttpPost("{connectorId}")]
    public async Task<IActionResult> ProcessWebhookWithConnector(
        string connectorId,
        [FromBody] SesarWebHookRequest request)
    {
      if (request?.Args == null)
      {
        return BadRequest("Invalid webhook data. Expected format: { \"args\": { \"HashKey\": \"...\", \"CryptedObject\": \"...\" } }");
      }

      if (string.IsNullOrEmpty(connectorId))
      {
        return BadRequest("Connector ID is required");
      }

      return await ProcessWithConnector(request.Args, connectorId);
    }

    [HttpPost("multi")]
    public async Task<IActionResult> ProcessWebhookMultiple(
        [FromQuery] string connectors,
        [FromBody] SesarWebHookRequest request)
    {
      if (request?.Args == null)
      {
        return BadRequest("Invalid webhook data. Expected format: { \"args\": { \"HashKey\": \"...\", \"CryptedObject\": \"...\" } }");
      }

      if (string.IsNullOrEmpty(connectors))
      {
        return BadRequest("At least one connector ID is required");
      }

      var processor = _config.WebhookProcessor;
      if (processor == null)
      {
        return StatusCode(500, new { Error = "Webhook processor not initialized. Check encryption key configuration." });
      }

      var connectorIds = connectors.Split(',');
      var results = await processor.ProcessWebhookWithMultipleConnectorsAsync(request.Args, connectorIds);

      return Ok(new
      {
        Success = true,
        Results = results
      });
    }

    [HttpPost("handler/multi")]
    public async Task<IActionResult> ProcessWebhookWithMultipleHandlers(
        [FromQuery] string handlers,
        [FromBody] SesarWebHookRequest request)
    {
      if (request?.Args == null)
      {
        return BadRequest("Invalid webhook data. Expected format: { \"args\": { \"HashKey\": \"...\", \"CryptedObject\": \"...\" } }");
      }

      if (string.IsNullOrEmpty(handlers))
      {
        return BadRequest("At least one handler ID is required. Usage: /api/webhook/handler/multi?handlers=handler1,handler2");
      }

      var processor = _config.WebhookProcessor;
      if (processor == null)
      {
        return StatusCode(500, new { Error = "Webhook processor not initialized. Check encryption key configuration." });
      }

      var handlerRegistry = _config.HandlerRegistry;
      var genericConnector = _config.GenericConnector;
      if (handlerRegistry == null || genericConnector == null)
      {
        return StatusCode(500, new { Error = "Handler system not initialized." });
      }

      if (!processor.ValidateAuthentication(request.Args.HashKey))
      {
        return StatusCode((int)HttpStatusCode.Unauthorized, IntegrationResult.Fail(
            "Authentication failed", "Invalid hash key", "multi-handler"));
      }

      string jsonPayload;
      try
      {
        jsonPayload = processor.DecryptPayload(request.Args.CryptedObject);
      }
      catch (System.Exception ex)
      {
        return StatusCode(500, IntegrationResult.Fail(
            "Decryption failed", ex.Message, "multi-handler"));
      }

      SecureExchangesSDK.Models.Messenging.StoreManifest manifest;
      try
      {
        manifest = SecureExchangesSDK.Helpers.SerializationHelper.DeserializeFromJson<SecureExchangesSDK.Models.Messenging.StoreManifest>(jsonPayload);
      }
      catch (System.Exception ex)
      {
        return StatusCode(500, IntegrationResult.Fail(
            "Deserialization failed", ex.Message, "multi-handler"));
      }

      var handlerIds = handlers.Split(',');
      var tasks = new List<Task<IntegrationResult>>();
      var unknownHandlers = new List<string>();

      foreach (var handlerId in handlerIds)
      {
        var id = handlerId.Trim();
        if (string.IsNullOrEmpty(id)) continue;

        if (!handlerRegistry.HandlerExists(id))
        {
          unknownHandlers.Add(id);
          continue;
        }

        var context = new WebhookContext
        {
          ConnectorId = id,
          Metadata = new Dictionary<string, object>
                    {
                        { "HandlerId", id }
                    },
          RawPayload = jsonPayload
        };

        tasks.Add(genericConnector.ProcessManifestAsync(manifest, context));
      }

      var results = new List<object>();

      foreach (var unknown in unknownHandlers)
      {
        results.Add(IntegrationResult.Fail("Handler not found", $"No handler registered with ID '{unknown}'", unknown));
      }

      if (tasks.Count > 0)
      {
        var handlerResults = await Task.WhenAll(tasks);
        foreach (var result in handlerResults)
        {
          results.Add(result);
        }
      }

      return Ok(new
      {
        Success = unknownHandlers.Count == 0 && tasks.Count > 0,
        TotalHandlers = handlerIds.Length,
        Processed = tasks.Count,
        Failed = unknownHandlers.Count,
        Results = results
      });
    }

    private async Task<IActionResult> ProcessWithConnector(SesarWebHook webhookData, string connectorId)
    {
      var processor = _config.WebhookProcessor;
      if (processor == null)
      {
        return StatusCode(500, new { Error = "Webhook processor not initialized. Check encryption key configuration." });
      }

      var settings = _config.GetConnectorSettings(connectorId);
      var connector = _config.ConnectorRegistry.GetOrCreateConnector(connectorId, settings);

      if (connector == null)
      {
        return NotFound();
      }

      var result = await processor.ProcessWebhookAsync(webhookData, connectorId);

      if (result.Success)
      {
        if (result.ItemsProcessed == 0)
        {
          // Succes sans action (ex. manifest sans destinataire de redirection) : en avertissement,
          // pour rester visible dans le journal Windows quand le service tourne sans console.
          _logger.LogWarning(
              "Webhook traite sans action. Connecteur={ConnectorId} Message={Message}",
              connectorId, result.Message);
        }
        else
        {
          _logger.LogInformation(
              "Webhook traite. Connecteur={ConnectorId} Message={Message} Traites={ItemsProcessed}",
              connectorId, result.Message, result.ItemsProcessed);
        }
        return Ok(result);
      }

      // Journal serveur : c'est ici que le detail des echecs (fichier manquant, dechiffrement en
      // echec, .eml non ecrit) est visible. SESAR ne lit pas le corps de la reponse : sur un 500,
      // il conserve son fichier de file d'attente et reessaie a la prochaine boucle.
      _logger.LogError(
          "Echec du webhook. Connecteur={ConnectorId} Message={Message} Echecs={ItemsFailed} Detail={ErrorDetails}",
          connectorId, result.Message, result.ItemsFailed, result.ErrorDetails);

      // Reponse a l'appelant : message generique. Le Message du connecteur contient des chemins
      // de fichiers et l'echec de validation de la HashKey passe aussi par ici : un appelant non
      // authentifie ne doit obtenir aucune information sur le systeme ni sur la cause du refus.
      return StatusCode(500, new
      {
        Success = false,
        Message = GenericFailureMessage,
        ConnectorId = connectorId
      });
    }
  }
}
