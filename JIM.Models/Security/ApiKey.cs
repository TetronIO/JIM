using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Security;

/// <summary>
/// Represents an API key for non-interactive authentication.
/// API keys provide an alternative to SSO for CI/CD pipelines, integration testing, and automation.
/// </summary>
[Index(nameof(KeyHash))]
[Index(nameof(KeyPrefix))]
public class ApiKey
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
    /// When the API key was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

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
    /// The roles assigned to this API key. These determine what actions the key can perform.
    /// </summary>
    public List<Role> Roles { get; set; } = [];
}
