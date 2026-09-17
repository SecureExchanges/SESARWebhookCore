using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SESARWebHook.Connectors.Dynamics;
using SESARWebHook.Connectors.FileSystem;
using SESARWebHook.Connectors.SharePoint;
using SESARWebHook.Connectors.ZohoCRM;
using SESARWebHook.Core.Configuration;
using SESARWebHook.Core.Connectors;
using SESARWebHook.Core.Services;
using System;

namespace SESARWebHook.API
{
  public class Program
  {
    public static void Main(string[] args)
    {
      var builder = WebApplication.CreateBuilder(args);

      // Permet l'execution comme service Windows. Sans effet lorsque l'application est lancee
      // en console. Fixe aussi le repertoire de base sur le dossier de l'executable : sans cela,
      // un service demarre avec C:\Windows\System32 comme repertoire courant et ne trouverait
      // ni appsettings.json ni connectors.secrets.json.
      builder.Host.UseWindowsService();

      // Configure JSON serializer to match SESAR's format
      builder.Services.AddControllers()
          .AddNewtonsoftJson(options =>
          {
            options.SerializerSettings.ContractResolver = new DefaultContractResolver();
            options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
          });

      // Initialize configuration helper
      WebHookConfigHelper.Initialize(builder.Configuration);

      // Initialize connectors
      var startup = new StartupConfig();
      startup.InitializeConnectors();

      // Register singletons for DI
      builder.Services.AddSingleton(startup.ConnectorRegistry);
      builder.Services.AddSingleton(startup.HandlerRegistry);
      builder.Services.AddSingleton(startup.GenericConnector);
      if (startup.WebhookProcessor != null)
      {
        builder.Services.AddSingleton(startup.WebhookProcessor);
      }
      builder.Services.AddSingleton(startup);

      var app = builder.Build();

      app.UseHttpsRedirection();
      app.MapControllers();

      app.Run();
    }
  }

  public class StartupConfig
  {
    public ConnectorRegistry ConnectorRegistry { get; private set; }
    public HandlerRegistry HandlerRegistry { get; private set; }
    public GenericConnector GenericConnector { get; private set; }
    public WebhookProcessor WebhookProcessor { get; private set; }
    public bool IsInitialized { get; private set; }
    public string InitializationError { get; private set; }

    public void InitializeConnectors()
    {
      ConnectorRegistry = new ConnectorRegistry();
      HandlerRegistry = new HandlerRegistry();

      // Register all available built-in connectors
      ConnectorRegistry.RegisterConnector<FileSystemConnector>();
      ConnectorRegistry.RegisterConnector<ZohoCRMConnector>();
      ConnectorRegistry.RegisterConnector<SharePointConnector>();
      ConnectorRegistry.RegisterConnector<DynamicsConnector>();

      // Register the Generic Connector (for custom client handlers)
      GenericConnector = new GenericConnector(HandlerRegistry);
      ConnectorRegistry.RegisterConnector("generic", typeof(GenericConnector));
      ConnectorRegistry.PreCacheConnectorInstance("generic", GenericConnector);

      // Scan for custom handler DLLs in the /Handlers/ folder
      var handlersPath = WebHookConfigHelper.HandlersPath;
      HandlerRegistry.ScanForHandlers(handlersPath);

      try
      {
        // Initialize the webhook processor with protected keys
        var key = WebHookConfigHelper.WebHookKey;
        var iv = WebHookConfigHelper.WebHookIV;

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(iv))
        {
          WebhookProcessor = new WebhookProcessor(ConnectorRegistry, key, iv);
          IsInitialized = true;
        }
        else
        {
          var keyStatus = string.IsNullOrEmpty(key) ? "MISSING" : "OK";
          var ivStatus = string.IsNullOrEmpty(iv) ? "MISSING" : "OK";
          InitializationError = $"No Webhook key found in connectors.secrets.json. " +
              $"Make sure that this file exists and that the keys are present.";
          IsInitialized = false;
        }
      }
      catch (Exception ex)
      {
        InitializationError = $"Secrets initialization error: {ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
        {
          InitializationError += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        }
        IsInitialized = false;
      }

      // Initialize the Generic Connector with its settings
      try
      {
        var genericSettings = GetConnectorSettings("generic");
        GenericConnector.Initialize(genericSettings);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Trace.TraceWarning($"GenericConnector initialization warning: {ex.Message}");
      }
    }

    public System.Collections.Generic.Dictionary<string, string> GetConnectorSettings(string connectorId)
    {
      return WebHookConfigHelper.GetConnectorSettings(connectorId);
    }
  }
}
