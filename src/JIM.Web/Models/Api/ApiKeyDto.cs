// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Security;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of an API key for list and detail views.
/// Does not include the full key (only the prefix).
/// </summary>
public class ApiKeyDto
{
    /// <summary>
    /// Unique identifier for the API key.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name for the API key.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the key's purpose.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Prefix of the key (e.g., "jim_ak_7f3a") for identification.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// When the API key was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the key stops working. Always set for a SCIM client's key; null only for an ordinary API key that never
    /// expires.
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
    /// Whether the API key is currently enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The roles assigned to this API key.
    /// </summary>
    public List<RoleDto> Roles { get; set; } = [];

    /// <summary>
    /// What the key may be used for: <c>None</c> for an ordinary API key authorised by its roles, or
    /// <c>ScimServiceProvider</c> for a SCIM client's key, which works only on the SCIM endpoint of one Connected
    /// System and carries no roles.
    /// </summary>
    public ApiKeyScope Scope { get; set; }

    /// <summary>
    /// The Connected System a scoped key works for; null for an ordinary API key.
    /// </summary>
    public int? ScopeConnectedSystemId { get; set; }

    /// <summary>
    /// Creates a DTO from an ApiKey entity.
    /// </summary>
    public static ApiKeyDto FromEntity(ApiKey entity)
    {
        return new ApiKeyDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            KeyPrefix = entity.KeyPrefix,
            CreatedAt = entity.Created,
            ExpiresAt = entity.ExpiresAt,
            LastUsedAt = entity.LastUsedAt,
            LastUsedFromIp = entity.LastUsedFromIp,
            IsEnabled = entity.IsEnabled,
            Roles = entity.Roles.Select(RoleDto.FromEntity).ToList(),
            Scope = entity.Scope,
            ScopeConnectedSystemId = entity.ScopeConnectedSystemId
        };
    }
}

/// <summary>
/// Response DTO returned when creating a new API key.
/// Includes the full key (shown only once).
/// </summary>
public class ApiKeyCreateResponseDto : ApiKeyDto
{
    /// <summary>
    /// The full API key. This is only returned on creation and cannot be retrieved again.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Creates the response for a key just created, carrying its secret.
    /// </summary>
    public static ApiKeyCreateResponseDto FromCreated(ApiKey entity, string key)
    {
        var dto = FromEntity(entity);
        return new ApiKeyCreateResponseDto
        {
            Id = dto.Id,
            Name = dto.Name,
            Description = dto.Description,
            KeyPrefix = dto.KeyPrefix,
            CreatedAt = dto.CreatedAt,
            ExpiresAt = dto.ExpiresAt,
            LastUsedAt = dto.LastUsedAt,
            LastUsedFromIp = dto.LastUsedFromIp,
            IsEnabled = dto.IsEnabled,
            Roles = dto.Roles,
            Scope = dto.Scope,
            ScopeConnectedSystemId = dto.ScopeConnectedSystemId,
            Key = key
        };
    }
}

/// <summary>
/// Request DTO for creating a new API key.
/// </summary>
public class ApiKeyCreateRequestDto
{
    /// <summary>
    /// Human-readable name for the API key.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the key's purpose.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// IDs of roles to assign to the API key.
    /// </summary>
    public List<int> RoleIds { get; set; } = [];

    /// <summary>
    /// Optional expiry date. Null means the key never expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Change reason must not exceed 1000 characters.")]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request DTO for updating an existing API key.
/// </summary>
public class ApiKeyUpdateRequestDto
{
    /// <summary>
    /// Human-readable name for the API key.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the key's purpose.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// IDs of roles to assign to the API key.
    /// </summary>
    public List<int> RoleIds { get; set; } = [];

    /// <summary>
    /// Optional expiry date. Null means the key never expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Whether the API key is currently enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    [StringLength(1000, ErrorMessage = "Change reason must not exceed 1000 characters.")]
    public string? ChangeReason { get; set; }
}
