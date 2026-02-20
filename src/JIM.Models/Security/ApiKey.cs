using JIM.Models.Activities;
using JIM.Models.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Security;

/// <summary>
/// Represents an API key for non-interactive authentication.
/// API keys provide an alternative to SSO for CI/CD pipelines, integration testing, and automation.
/// </summary>
[Index(nameof(KeyHash))]
[Index(nameof(KeyPrefix))]
public class ApiKey : IAuditable
{
    /// <summary>
    /// Unique identifier for the API key.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name for the API key (e.g., "Integration Test Key", "CI/CD Pipeline").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description providing additional context about the key's purpose.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// SHA256 hash of the API key. The plaintext key is never stored.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// First 8 characters of the key (e.g., "jim_ak_7f3a") for identification in logs and UI.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// When the entity was created (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// Optional expiry date. Null means the key never expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// When the API key was last used for authentication.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// IP address from which the key was last used.
    /// </summary>
    public string? LastUsedFromIp { get; set; }

    /// <summary>
    /// Whether the API key is currently enabled. Disabled keys cannot authenticate.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether this is an infrastructure key created automatically from the JIM_INFRASTRUCTURE_API_KEY environment variable.
    /// Infrastructure keys are intended for initial CI/CD setup and should be deleted after the environment is configured.
    /// </summary>
    public bool IsInfrastructureKey { get; set; }

    /// <summary>
    /// The roles assigned to this API key. These determine what actions the key can perform.
    /// </summary>
    public List<Role> Roles { get; set; } = [];
}
