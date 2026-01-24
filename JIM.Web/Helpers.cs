using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
namespace JIM.Web;

public static class Helpers
{
    /// <summary>
    /// Converts a string to a format that can be used as a Url parameter.
    /// </summary>
    public static string ConvertToUrlParam(string textToConvert)
    {
        return textToConvert.Replace(" ", "-").ToLower();
    }

    /// <summary>
    /// Converts a string taken from an Url parameter back to the plain text version.
    /// Note: This does not change case from lower-case URL param to whatever it was originally.
    /// </summary>
    public static string ConvertFromUrlParam(string urlParam)
    {
        return urlParam.Replace("-", " ");
    }

    /// <summary>
    /// Returns the MetaverseObject for the currently signed in JIM.Web user.
    /// </summary>
    public static async Task<MetaverseObject> GetUserAsync(JimApplication jimApplication, Task<AuthenticationState>? authenticationStateTask)
    {
        if (authenticationStateTask == null)
            throw new Exception("Authentication state not available");

        var userId = IdentityUtilities.GetUserId((await authenticationStateTask).User);
        var user = await jimApplication.Metaverse.GetMetaverseObjectAsync(userId);
        return user ?? throw new Exception($"User not found for user id: {userId}");
    }

    /// <summary>
    /// Extension method that converts a DateTime into a more human-readable string.
    /// </summary>
    public static string ToFriendlyDate(this DateTime dateTime)
    {
        return $"{dateTime.ToShortDateString()} ({dateTime.ToShortTimeString()})";
    }

    /// <summary>
    /// Extension method that converts a DateTime into a relative time string (e.g., "2 hours ago", "just now").
    /// </summary>
    public static string ToRelativeTime(this DateTime dateTime)
    {
        var timeSpan = DateTime.UtcNow - dateTime.ToUniversalTime();

        if (timeSpan.TotalSeconds < 60)
            return "just now";
        if (timeSpan.TotalMinutes < 60)
        {
            var minutes = (int)timeSpan.TotalMinutes;
            return $"{minutes} {(minutes == 1 ? "minute" : "minutes")} ago";
        }
        if (timeSpan.TotalHours < 24)
        {
            var hours = (int)timeSpan.TotalHours;
            return $"{hours} {(hours == 1 ? "hour" : "hours")} ago";
        }
        if (timeSpan.TotalDays < 30)
        {
            var days = (int)timeSpan.TotalDays;
            return $"{days} {(days == 1 ? "day" : "days")} ago";
        }
        if (timeSpan.TotalDays < 365)
        {
            var months = (int)(timeSpan.TotalDays / 30);
            return $"{months} {(months == 1 ? "month" : "months")} ago";
        }

        var years = (int)(timeSpan.TotalDays / 365);
        return $"{years} {(years == 1 ? "year" : "years")} ago";
    }

    /// <summary>
    /// Extension method that converts a TimeSpan into a human-readable string with abbreviated units.
    /// Examples: "143 ms", "14 sec, 210 ms", "2 min, 15 sec"
    /// </summary>
    public static string ToAbbreviatedString(this TimeSpan timeSpan, int precision = 2)
    {
        var parts = new List<string>();

        if (timeSpan.Days > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Days} day{(timeSpan.Days == 1 ? "" : "s")}");
        if (timeSpan.Hours > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Hours} hr{(timeSpan.Hours == 1 ? "" : "s")}");
        if (timeSpan.Minutes > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Minutes} min");
        if (timeSpan.Seconds > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Seconds} sec");
        if (timeSpan.Milliseconds > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Milliseconds} ms");

        return parts.Count > 0 ? string.Join(", ", parts) : "0 ms";
    }

    /// <summary>
    /// Extension method that converts a TimeSpan into a casual, rounded approximation using the largest appropriate unit.
    /// Examples: "143 ms", "~14 sec", "~16 sec", "~2 min"
    /// </summary>
    public static string ToCasualString(this TimeSpan timeSpan)
    {
        // Round to the largest appropriate unit
        if (timeSpan.TotalDays >= 1)
        {
            var days = Math.Round(timeSpan.TotalDays);
            return $"~{days} day{(days == 1 ? "" : "s")}";
        }
        if (timeSpan.TotalHours >= 1)
        {
            var hours = Math.Round(timeSpan.TotalHours);
            return $"~{hours} hr{(hours == 1 ? "" : "s")}";
        }
        if (timeSpan.TotalMinutes >= 1)
            return $"~{Math.Round(timeSpan.TotalMinutes)} min";
        if (timeSpan.TotalSeconds >= 1)
            return $"~{Math.Round(timeSpan.TotalSeconds)} sec";

        return $"{Math.Round(timeSpan.TotalMilliseconds)} ms";
    }

    #region mudblazor related
    public static Color GetActivityMudBlazorColorForStatus(ActivityStatus status)
    {
        return status switch
        {
            ActivityStatus.Complete => Color.Success,
            ActivityStatus.InProgress => Color.Primary,
            ActivityStatus.CompleteWithWarning => Color.Warning,
            ActivityStatus.CompleteWithError => Color.Tertiary,
            ActivityStatus.FailedWithError => Color.Error,
            _ => Color.Default,
        };
    }

    public static Color GetActivityMudBlazorColorForOperation(ActivityTargetOperationType operation)
    {
        return operation switch
        {
            ActivityTargetOperationType.Create => Color.Success,
            ActivityTargetOperationType.Delete => Color.Error,
            ActivityTargetOperationType.Clear => Color.Error,
            ActivityTargetOperationType.Update => Color.Info,
            ActivityTargetOperationType.Read => Color.Default,
            ActivityTargetOperationType.Execute => Color.Primary,
            ActivityTargetOperationType.ImportHierarchy => Color.Primary,
            ActivityTargetOperationType.ImportSchema => Color.Primary,
            ActivityTargetOperationType.Revert => Color.Warning,
            _ => Color.Default,
        };
    }

    public static Color GetRunItemMudBlazorColorForType(ObjectChangeType objectChangeType)
    {
        return objectChangeType switch
        {
            // Import (CSO operations)
            ObjectChangeType.Added => Color.Success,
            ObjectChangeType.Updated => Color.Info,
            ObjectChangeType.Deleted => Color.Error,

            // Sync (MVO operations)
            ObjectChangeType.Projected => Color.Primary,
            ObjectChangeType.Joined => Color.Secondary,
            ObjectChangeType.AttributeFlow => Color.Tertiary,
            ObjectChangeType.Disconnected => Color.Warning,
            ObjectChangeType.DriftCorrection => Color.Warning,

            // Export
            ObjectChangeType.Provisioned => Color.Success,
            ObjectChangeType.Exported => Color.Info,
            ObjectChangeType.Deprovisioned => Color.Error,

            // Pending Export visibility
            ObjectChangeType.PendingExport => Color.Warning,
            ObjectChangeType.PendingExportConfirmed => Color.Success,

            // Other
            ObjectChangeType.NoChange => Color.Default,
            _ => Color.Default,
        };
    }

    public static Color GetMudBlazorColorForValueChangeType(ValueChangeType valueChangeType)
    {
        return valueChangeType switch
        {
            ValueChangeType.Add => Color.Primary,
            ValueChangeType.Remove => Color.Secondary,
            ValueChangeType.NotSet => Color.Error,
            _ => Color.Default,
        };
    }

    public static Color GetPendingExportStatusColor(PendingExportStatus status)
    {
        return status switch
        {
            PendingExportStatus.Pending => Color.Info,
            PendingExportStatus.Executing => Color.Primary,
            PendingExportStatus.ExportNotConfirmed => Color.Warning,
            PendingExportStatus.Failed => Color.Error,
            PendingExportStatus.Exported => Color.Success,
            _ => Color.Default,
        };
    }

    public static Color GetPendingExportChangeTypeColor(PendingExportChangeType changeType)
    {
        return changeType switch
        {
            PendingExportChangeType.Create => Color.Primary,
            PendingExportChangeType.Update => Color.Info,
            PendingExportChangeType.Delete => Color.Error,
            _ => Color.Default,
        };
    }
    #endregion

    #region Initiator Icon Helpers
    /// <summary>
    /// Gets the MudBlazor icon for an activity initiator type.
    /// </summary>
    public static string GetInitiatorIcon(ActivityInitiatorType initiatorType)
    {
        return initiatorType switch
        {
            ActivityInitiatorType.User => Icons.Material.Filled.Person,
            ActivityInitiatorType.ApiKey => Icons.Material.Filled.Key,
            _ => Icons.Material.Filled.HelpOutline
        };
    }

    /// <summary>
    /// Gets the MudBlazor icon for an initiator type string.
    /// Accepts "User", "ApiKey", "Import", or other values.
    /// </summary>
    public static string GetInitiatorIcon(string? initiatorType)
    {
        return initiatorType switch
        {
            "User" => Icons.Material.Filled.Person,
            "ApiKey" => Icons.Material.Filled.Key,
            "Import" => Icons.Material.Filled.Input,
            _ => Icons.Material.Filled.HelpOutline
        };
    }
    #endregion

    #region Run Type Helpers
    /// <summary>
    /// Gets the display title for the results section based on run type.
    /// </summary>
    public static string GetRunTypeResultsTitle(ConnectedSystemRunType? runType)
    {
        return runType switch
        {
            ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport => "Import Results",
            ConnectedSystemRunType.FullSynchronisation or ConnectedSystemRunType.DeltaSynchronisation => "Synchronisation Results",
            ConnectedSystemRunType.Export => "Export Results",
            _ => "Results"
        };
    }

    /// <summary>
    /// Gets the relevant ObjectChangeTypes for a given run type.
    /// </summary>
    public static IEnumerable<ObjectChangeType> GetChangeTypesForRunType(ConnectedSystemRunType? runType)
    {
        return runType switch
        {
            ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport =>
                new[] { ObjectChangeType.Added, ObjectChangeType.Updated, ObjectChangeType.Deleted },
            ConnectedSystemRunType.FullSynchronisation or ConnectedSystemRunType.DeltaSynchronisation =>
                new[] { ObjectChangeType.Projected, ObjectChangeType.Joined, ObjectChangeType.AttributeFlow, ObjectChangeType.Disconnected },
            ConnectedSystemRunType.Export =>
                new[] { ObjectChangeType.Provisioned, ObjectChangeType.Exported, ObjectChangeType.Deprovisioned },
            _ => Array.Empty<ObjectChangeType>()
        };
    }

    /// <summary>
    /// Gets the stat count for a specific change type from the stats model.
    /// </summary>
    public static int GetStatCountForChangeType(ActivityRunProfileExecutionStats stats, ObjectChangeType changeType)
    {
        return changeType switch
        {
            // Import
            ObjectChangeType.Added => stats.TotalCsoAdds,
            ObjectChangeType.Updated => stats.TotalCsoUpdates,
            ObjectChangeType.Deleted => stats.TotalCsoDeletes,
            // Sync
            ObjectChangeType.Projected => stats.TotalProjections,
            ObjectChangeType.Joined => stats.TotalJoins,
            ObjectChangeType.AttributeFlow => stats.TotalAttributeFlows,
            ObjectChangeType.Disconnected => stats.TotalDisconnections,
            // Export
            ObjectChangeType.Provisioned => stats.TotalProvisioned,
            ObjectChangeType.Exported => stats.TotalExported,
            ObjectChangeType.Deprovisioned => stats.TotalDeprovisioned,
            _ => 0
        };
    }

    /// <summary>
    /// Gets a human-readable display name for a change type with proper spacing.
    /// </summary>
    public static string GetChangeTypeDisplayName(ObjectChangeType changeType)
    {
        return changeType switch
        {
            ObjectChangeType.AttributeFlow => "Attribute Flow",
            ObjectChangeType.DriftCorrection => "Drift Correction",
            ObjectChangeType.PendingExport => "Pending Export",
            ObjectChangeType.PendingExportConfirmed => "Pending Export Confirmed",
            _ => changeType.ToString()
        };
    }
    #endregion
}