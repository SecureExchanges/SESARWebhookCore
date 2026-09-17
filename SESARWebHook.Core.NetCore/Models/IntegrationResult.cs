using System;
using System.Collections.Generic;

namespace SESARWebHook.Core.Models
{
  /// <summary>
  /// Result of an integration operation
  /// </summary>
  public class IntegrationResult
  {
    /// <summary>
    /// Indicates if the operation was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Human-readable message describing the result
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// Error details if the operation failed.
    ///
    /// USAGE INTERNE UNIQUEMENT : journalise cote serveur, jamais renvoye a l'appelant.
    /// Ce champ contient des traces d'exception, des chemins de fichiers et des reponses brutes
    /// de systemes tiers. Les deux attributs JsonIgnore garantissent qu'il ne peut pas etre
    /// serialise dans une reponse HTTP, quel que soit le serialiseur utilise (l'API utilise
    /// Newtonsoft, d'autres projets System.Text.Json).
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string ErrorDetails { get; set; }

    /// <summary>
    /// The connector ID that processed this request
    /// </summary>
    public string ConnectorId { get; set; }

    /// <summary>
    /// Timestamp when the operation completed
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Optional external reference ID from the target system
    /// </summary>
    public string ExternalReferenceId { get; set; }

    /// <summary>
    /// Additional metadata from the integration
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; }

    /// <summary>
    /// Number of items processed
    /// </summary>
    public int ItemsProcessed { get; set; }

    /// <summary>
    /// Number of items that failed
    /// </summary>
    public int ItemsFailed { get; set; }

    public IntegrationResult()
    {
      Timestamp = DateTime.UtcNow;
      Metadata = new Dictionary<string, object>();
    }

    /// <summary>
    /// Creates a successful result
    /// </summary>
    public static IntegrationResult Ok(string message = "Operation completed successfully", string connectorId = null)
    {
      return new IntegrationResult
      {
        Success = true,
        Message = message,
        ConnectorId = connectorId
      };
    }

    /// <summary>
    /// Creates a failed result
    /// </summary>
    public static IntegrationResult Fail(string message, string errorDetails = null, string connectorId = null)
    {
      return new IntegrationResult
      {
        Success = false,
        Message = message,
        ErrorDetails = errorDetails,
        ConnectorId = connectorId
      };
    }
  }
}
