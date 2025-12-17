using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
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
    /// Extension method that converts a TimeSpan into a human-readable string with abbreviated units.
    /// Examples: "143 ms", "14 s, 210 ms", "2 m, 15 s"
    /// </summary>
    public static string ToAbbreviatedString(this TimeSpan timeSpan, int precision = 2)
    {
        var parts = new List<string>();

        if (timeSpan.Days > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Days} d");
        if (timeSpan.Hours > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Hours} h");
        if (timeSpan.Minutes > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Minutes} m");
        if (timeSpan.Seconds > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Seconds} s");
        if (timeSpan.Milliseconds > 0 && parts.Count < precision)
            parts.Add($"{timeSpan.Milliseconds} ms");

        return parts.Count > 0 ? string.Join(", ", parts) : "0 ms";
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

    public static Color GetRunItemMudBlazorColorForType(ObjectChangeType objectChangeType)
    {
        return objectChangeType switch
        {
            ObjectChangeType.Create => Color.Primary,
            ObjectChangeType.Update => Color.Default,
            ObjectChangeType.Delete => Color.Error,
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
            PendingExportStatus.ExportNotImported => Color.Warning,
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
}