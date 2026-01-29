using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;

namespace JIM.Application.Utilities;

/// <summary>
/// Stamps standardised audit fields on IAuditable entities.
/// Called by application servers before persisting entities to ensure consistent audit tracking.
/// </summary>
public static class AuditHelper
{
    /// <summary>
    /// Stamps Created audit fields for a user-initiated creation.
    /// </summary>
    public static void SetCreated(IAuditable entity, MetaverseObject? user)
    {
        entity.Created = DateTime.UtcNow;
        if (user != null)
        {
            entity.CreatedByType = ActivityInitiatorType.User;
            entity.CreatedById = user.Id;
            entity.CreatedByName = user.DisplayName;
        }
    }

    /// <summary>
    /// Stamps Created audit fields for an API key-initiated creation.
    /// </summary>
    public static void SetCreated(IAuditable entity, ApiKey apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        entity.Created = DateTime.UtcNow;
        entity.CreatedByType = ActivityInitiatorType.ApiKey;
        entity.CreatedById = apiKey.Id;
        entity.CreatedByName = apiKey.Name;
    }

    /// <summary>
    /// Stamps Created audit fields for a system-initiated creation (seeding, initialisation).
    /// </summary>
    public static void SetCreatedBySystem(IAuditable entity)
    {
        entity.Created = DateTime.UtcNow;
        entity.CreatedByType = ActivityInitiatorType.System;
        entity.CreatedById = null;
        entity.CreatedByName = "System";
    }

    /// <summary>
    /// Stamps LastUpdated audit fields for a user-initiated update.
    /// </summary>
    public static void SetUpdated(IAuditable entity, MetaverseObject? user)
    {
        entity.LastUpdated = DateTime.UtcNow;
        if (user != null)
        {
            entity.LastUpdatedByType = ActivityInitiatorType.User;
            entity.LastUpdatedById = user.Id;
            entity.LastUpdatedByName = user.DisplayName;
        }
    }

    /// <summary>
    /// Stamps LastUpdated audit fields for an API key-initiated update.
    /// </summary>
    public static void SetUpdated(IAuditable entity, ApiKey apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        entity.LastUpdated = DateTime.UtcNow;
        entity.LastUpdatedByType = ActivityInitiatorType.ApiKey;
        entity.LastUpdatedById = apiKey.Id;
        entity.LastUpdatedByName = apiKey.Name;
    }

    /// <summary>
    /// Stamps LastUpdated audit fields for a system-initiated update.
    /// </summary>
    public static void SetUpdatedBySystem(IAuditable entity)
    {
        entity.LastUpdated = DateTime.UtcNow;
        entity.LastUpdatedByType = ActivityInitiatorType.System;
        entity.LastUpdatedById = null;
        entity.LastUpdatedByName = "System";
    }

    /// <summary>
    /// Stamps Created audit fields using initiator triad.
    /// </summary>
    public static void SetCreated(IAuditable entity, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName)
    {
        entity.Created = DateTime.UtcNow;
        entity.CreatedByType = initiatorType;
        entity.CreatedById = initiatorId;
        entity.CreatedByName = initiatorName ?? (initiatorType == ActivityInitiatorType.System ? "System" : "Unknown");
    }

    /// <summary>
    /// Stamps LastUpdated audit fields using initiator triad.
    /// </summary>
    public static void SetUpdated(IAuditable entity, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName)
    {
        entity.LastUpdated = DateTime.UtcNow;
        entity.LastUpdatedByType = initiatorType;
        entity.LastUpdatedById = initiatorId;
        entity.LastUpdatedByName = initiatorName ?? (initiatorType == ActivityInitiatorType.System ? "System" : "Unknown");
    }
}
