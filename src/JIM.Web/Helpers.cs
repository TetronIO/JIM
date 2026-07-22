// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Logic;
using JIM.Utilities;
using JIM.Web.Causality;
using JIM.Web.Models;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
namespace JIM.Web;

/// <summary>
/// Represents the display status of an External ID when the CSO no longer exists
/// but an external ID snapshot is available.
/// </summary>
public enum ExternalIdStatus
{
    /// <summary>The object was rejected during import due to an error.</summary>
    Rejected,
    /// <summary>The object was detected as deleted and is pending removal during the next synchronisation.</summary>
    PendingRemoval,
    /// <summary>The object has been deleted.</summary>
    Deleted
}

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
    /// Matches an RFC reference such as "RFC2256" or "RFC 4519" as a whole word.
    /// The number group is used to build the IETF Datatracker Url.
    /// </summary>
    private static readonly Regex RfcReferenceRegex = new(
        @"\bRFC\s?(\d{3,5})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Converts any RFC references within a text value (e.g. "RFC2256: business category") into
    /// hyperlinks to the corresponding IETF Datatracker page, preserving the original text of each
    /// reference. All non-link content is HTML-encoded, so the result is safe to render via
    /// <c>@((MarkupString)...)</c>.
    /// </summary>
    public static string LinkifyRfcReferences(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // HTML-encode first so that any markup in the source text is neutralised; the regex still
        // matches because encoding does not alter the ASCII "RFC" token or its digits.
        var encoded = WebUtility.HtmlEncode(text);

        return RfcReferenceRegex.Replace(encoded, match =>
        {
            var number = match.Groups[1].Value;
            return $"<a class=\"mud-link mud-primary-text\" href=\"https://datatracker.ietf.org/doc/html/rfc{number}\" target=\"_blank\" rel=\"noopener noreferrer\">{match.Value}</a>";
        });
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
    /// Extension method that converts a DateTime into the site-wide human-friendly full date/time string
    /// (e.g. "12 Jul 2026 14:30:00"). Unambiguous and culture-independent, unlike the short date/time
    /// formats. Callers are responsible for calling <see cref="DateTime.ToLocalTime"/> first if the value
    /// should be displayed in the user's local time rather than UTC.
    /// </summary>
    public static string ToFriendlyDate(this DateTime dateTime)
    {
        return dateTime.ToString("dd MMM yyyy HH:mm:ss");
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
            ActivityTargetOperationType.Update => Color.Tertiary,
            ActivityTargetOperationType.Read => Color.Default,
            ActivityTargetOperationType.Execute => Color.Info,
            ActivityTargetOperationType.ImportHierarchy => Color.Secondary,
            ActivityTargetOperationType.ImportSchema => Color.Secondary,
            ActivityTargetOperationType.Revert => Color.Warning,
            ActivityTargetOperationType.Authenticate => Color.Info,
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
            ObjectChangeType.AttributeFlow => Color.Secondary,
            ObjectChangeType.Disconnected => Color.Warning,
            ObjectChangeType.DisconnectedOutOfScope => Color.Warning,
            ObjectChangeType.OutOfScopeRetainJoin => Color.Info,
            ObjectChangeType.DriftCorrection => Color.Warning,

            // Export
            ObjectChangeType.Exported => Color.Info,
            ObjectChangeType.Deprovisioned => Color.Error,

            // Pending Export visibility
            ObjectChangeType.PendingExport => Color.Warning,
            ObjectChangeType.PendingExportConfirmed => Color.Success,

            // Direct creation
            ObjectChangeType.Created => Color.Success,

            // Other
            ObjectChangeType.NoChange => Color.Default,
            _ => Color.Default,
        };
    }

    /// <summary>
    /// Returns a human-readable tooltip description for an External ID status chip.
    /// These statuses appear when the CSO no longer exists but an external ID snapshot is available.
    /// </summary>
    public static string GetExternalIdStatusDescription(ExternalIdStatus status)
    {
        return status switch
        {
            ExternalIdStatus.Rejected =>
                "This object was not created due to an import error. The external ID shown is from the source data.",
            ExternalIdStatus.PendingRemoval =>
                "This object has been detected as deleted from the source system and is pending removal during the next synchronisation.",
            ExternalIdStatus.Deleted =>
                "This object has been deleted. The external ID shown is preserved from when this operation was recorded.",
            _ => "The external ID shown is preserved from when this operation was recorded."
        };
    }

    /// <summary>
    /// Returns a human-readable tooltip description for an operation type, explaining what
    /// the operation means in context. The <paramref name="isSyncContext"/> parameter is used
    /// to disambiguate operations that have different meanings during import vs synchronisation.
    /// </summary>
    public static string GetOperationDescription(ObjectChangeType objectChangeType, bool isSyncContext = false)
    {
        return objectChangeType switch
        {
            // Import operations
            ObjectChangeType.Added =>
                "A new Connected System Object (CSO) was discovered in the source system and added to the connector space.",
            ObjectChangeType.Updated =>
                "An existing Connected System Object (CSO) was updated with changed attribute values from the source system.",
            ObjectChangeType.Deleted when !isSyncContext =>
                "The object was detected as deleted from the source system. It is now pending removal during the next synchronisation.",
            ObjectChangeType.Deleted when isSyncContext =>
                "The deletion of this object has been processed during synchronisation. Associated metaverse links and data have been updated.",

            // Sync operations
            ObjectChangeType.Projected =>
                "A new Metaverse Object (MVO) was created because no existing match was found. The CSO's attributes were projected into the metaverse.",
            ObjectChangeType.Joined =>
                "The Connected System Object (CSO) was matched to an existing Metaverse Object (MVO) using the configured join rules.",
            ObjectChangeType.AttributeFlow =>
                "Attribute values were flowed from the Connected System Object to the Metaverse Object according to the Synchronisation Rule mappings.",
            ObjectChangeType.Disconnected =>
                "The Connected System Object (CSO) was disconnected from its Metaverse Object (MVO). Attribute Flow has stopped.",
            ObjectChangeType.DisconnectedOutOfScope =>
                "The object fell out of scope of the import Synchronisation Rule scoping criteria and was disconnected from the metaverse.",
            ObjectChangeType.OutOfScopeRetainJoin =>
                "The object fell out of scope but the join was retained. Attribute Flow has stopped, but the link is preserved for future re-scoping.",
            ObjectChangeType.DriftCorrection =>
                "Drift was detected: the target system's values differed from the expected metaverse values. A corrective Pending Export was created to restore the expected state.",

            // Export operations
            ObjectChangeType.Exported =>
                "The pending attribute changes were successfully exported to the target Connected System.",
            ObjectChangeType.Deprovisioned =>
                "The object was deleted from the target Connected System as part of deprovisioning.",

            // Pending Export visibility
            ObjectChangeType.PendingExport =>
                "A Pending Export is staged and waiting for the next export run to apply changes to the target system.",
            ObjectChangeType.PendingExportConfirmed =>
                "The Pending Export was confirmed during the confirming import. The exported values matched the imported values.",

            // Direct creation
            ObjectChangeType.Created =>
                "The Metaverse Object was created directly (e.g. via data generation or the admin interface) rather than through synchronisation.",

            // Other
            ObjectChangeType.NoChange =>
                "The object was evaluated but no changes were necessary. The existing values already match the expected state.",
            _ => "An operation was performed on this object."
        };
    }

    /// <summary>
    /// Returns the canonical Material icon for an operation type (ObjectChangeType).
    /// Use this everywhere an operation icon is needed to ensure consistency across the UI.
    /// </summary>
    public static string GetOperationIcon(ObjectChangeType objectChangeType)
    {
        return objectChangeType switch
        {
            // Import operations
            ObjectChangeType.Added => Icons.Material.Filled.Add,
            ObjectChangeType.Updated => Icons.Material.Filled.Edit,
            ObjectChangeType.Deleted => Icons.Material.Filled.Delete,

            // Sync operations
            ObjectChangeType.Projected => Icons.Material.Filled.AirlineStops,
            ObjectChangeType.Joined => Icons.Material.Filled.Link,
            ObjectChangeType.AttributeFlow => Icons.Material.Filled.SyncAlt,
            ObjectChangeType.Disconnected => Icons.Material.Filled.LinkOff,
            ObjectChangeType.DisconnectedOutOfScope => Icons.Material.Filled.FilterAltOff,
            ObjectChangeType.OutOfScopeRetainJoin => Icons.Material.Filled.FilterAlt,
            ObjectChangeType.DriftCorrection => Icons.Material.Filled.CompareArrows,

            // Export operations
            ObjectChangeType.Exported => Icons.Material.Filled.Output,
            ObjectChangeType.Deprovisioned => Icons.Material.Filled.CloudOff,

            // Pending Export visibility
            ObjectChangeType.PendingExport => Icons.Material.Filled.Schedule,
            ObjectChangeType.PendingExportConfirmed => Icons.Material.Filled.CheckCircle,

            // Direct creation
            ObjectChangeType.Created => Icons.Material.Filled.AddCircleOutline,

            // Other
            ObjectChangeType.NoChange => Icons.Material.Filled.CheckCircle,
            _ => Icons.Material.Filled.Info
        };
    }

    /// <summary>
    /// Returns the canonical Material icon for an External ID status chip.
    /// </summary>
    public static string GetExternalIdStatusIcon(ExternalIdStatus status)
    {
        return status switch
        {
            ExternalIdStatus.Rejected => Icons.Material.Filled.Block,
            ExternalIdStatus.PendingRemoval => Icons.Material.Filled.Schedule,
            ExternalIdStatus.Deleted => Icons.Material.Filled.Delete,
            _ => Icons.Material.Filled.Info
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

    /// <summary>
    /// Returns a MudBlazor colour for the attribute data type chip.
    /// </summary>
    public static Color GetAttributeTypeChipColour(AttributeDataType type)
    {
        return type switch
        {
            AttributeDataType.Binary => Color.Default,
            AttributeDataType.Boolean => Color.Info,
            AttributeDataType.DateTime => Color.Secondary,
            AttributeDataType.Guid => Color.Warning,
            AttributeDataType.Number => Color.Success,
            AttributeDataType.LongNumber => Color.Success,
            AttributeDataType.Text => Color.Tertiary,
            AttributeDataType.Reference => Color.Primary,
            _ => Color.Default
        };
    }

    /// <summary>
    /// Returns a MudBlazor colour for attribute plurality (single vs multi-valued).
    /// </summary>
    public static Color GetAttributePluralityChipColour(AttributePlurality plurality)
    {
        return plurality switch
        {
            AttributePlurality.SingleValued => Color.Default,
            AttributePlurality.MultiValued => Color.Info,
            _ => Color.Default
        };
    }

    /// <summary>
    /// Returns a MudBlazor colour for Attribute Flow mapping type chips.
    /// </summary>
    public static Color GetMappingTypeChipColour(SyncRuleMappingSourcesType sourceType)
    {
        return sourceType switch
        {
            SyncRuleMappingSourcesType.AttributeMapping => Color.Info,
            SyncRuleMappingSourcesType.ExpressionMapping => Color.Tertiary,
            SyncRuleMappingSourcesType.AdvancedMapping => Color.Warning,
            _ => Color.Default
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
    /// Gets the in-app link to a security principal that initiated an activity, or null when the principal cannot be
    /// linked (no id, or a System / unattributed initiator). Used to make initiator names clickable in change history.
    /// </summary>
    public static string? GetInitiatorHref(ActivityInitiatorType initiatorType, Guid? initiatedById)
    {
        if (initiatedById == null)
            return null;

        return initiatorType switch
        {
            ActivityInitiatorType.User => $"/t/users/v/{initiatedById}",
            ActivityInitiatorType.ApiKey => $"/admin/apikeys/{initiatedById}",
            _ => null
        };
    }

    /// <summary>
    /// Gets the in-app link to the surface where an Object Matching Rule is managed, or null when neither owning-object
    /// id is known (e.g. an activity recorded before rules were linked to their owner). A rule has no page of its own:
    /// an Advanced Mode rule lives on its Synchronisation Rule's Matching tab, a Simple Mode rule on its Connected
    /// System's Matching tab. Used to make matching-rule activity targets clickable.
    /// </summary>
    public static string? GetObjectMatchingRuleHref(int? syncRuleId, int? connectedSystemId)
    {
        if (syncRuleId.HasValue)
            return $"/admin/sync-rules/{syncRuleId}?t=matching";
        if (connectedSystemId.HasValue)
            return $"/admin/connected-systems/{connectedSystemId}/?t=matching";
        return null;
    }

    /// <summary>
    /// Gets the in-app link for a Synchronisation Rule activity target, or null when the rule id is unknown.
    /// Attribute Flow mapping activities carry the rule's target type (a mapping has no target type of its own) with
    /// a "Mapping to ..." name; their target deep-links to the rule's Attribute Flow tab, where mappings are managed.
    /// </summary>
    public static string? GetSyncRuleActivityHref(int? syncRuleId, string? targetName)
    {
        if (!syncRuleId.HasValue)
            return null;
        return targetName?.StartsWith(Activity.SyncRuleMappingTargetNamePrefix, StringComparison.Ordinal) == true
            ? $"/admin/sync-rules/{syncRuleId}?t=attribute-flow"
            : $"/admin/sync-rules/{syncRuleId}";
    }

    /// <summary>
    /// Gets the in-app link for a Connected System activity target, or null when the system id is unknown.
    /// Operations whose subject lives on a specific tab deep-link there: schema imports to the Schema tab, hierarchy
    /// imports to the Partitions &amp; Containers tab.
    /// </summary>
    public static string? GetConnectedSystemActivityHref(int? connectedSystemId, ActivityTargetOperationType operationType)
    {
        if (!connectedSystemId.HasValue)
            return null;
        var basePath = $"/admin/connected-systems/{connectedSystemId}/";
        return operationType switch
        {
            ActivityTargetOperationType.ImportSchema => basePath + "?t=schema",
            ActivityTargetOperationType.ImportHierarchy => basePath + "?t=partitions-containers",
            _ => basePath
        };
    }

    /// <summary>
    /// Gets the in-app link for a Service Setting activity target, deep-linking to the Service Settings page with
    /// the setting's display name pre-filled into the search box, or null when the target name is unknown. Service
    /// Settings have no page of their own and no numeric/GUID id used by the UI, so the display name recorded on
    /// the activity (see <c>ServiceSettingsServer</c>, which always sets <c>TargetName</c> to the setting's
    /// <c>DisplayName</c>) doubles as the search term, matching <see cref="ServiceSetting.DisplayName"/> filtering
    /// on the settings table.
    /// </summary>
    public static string? GetServiceSettingActivityHref(string? targetName)
    {
        return string.IsNullOrEmpty(targetName)
            ? null
            : $"/admin/settings?search={Uri.EscapeDataString(targetName)}";
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

    /// <summary>
    /// Resolves a MudBlazor icon field name (e.g., "Person", "Groups") stored on a MetaverseObjectType
    /// to the actual MudBlazor icon SVG path string. Returns a fallback icon if the name is null or unrecognised.
    /// </summary>
    public static string ResolveObjectTypeIcon(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName))
            return Icons.Material.Filled.Category;

        return IconLookup.TryGetValue(iconName, out var icon) ? icon : Icons.Material.Filled.Category;
    }

    private static readonly Dictionary<string, string> IconLookup = BuildIconLookup();

    private static Dictionary<string, string> BuildIconLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in typeof(Icons.Material.Filled).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType == typeof(string) && field.GetValue(null) is string value)
                lookup[field.Name] = value;
        }
        return lookup;
    }
    #endregion

    #region Expression Syntax Highlighting

    /// <summary>
    /// Highlights a DynamicExpresso expression with HTML span elements for syntax colouring.
    /// Returns HTML markup string intended for use with <c>@((MarkupString)...)</c>.
    /// </summary>
    public static string HighlightExpression(string expression)
    {
        if (string.IsNullOrEmpty(expression))
            return string.Empty;

        var result = new StringBuilder();

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];

            // String literals
            if (c == '"')
            {
                var end = FindClosingQuote(expression, i);
                var literal = WebUtility.HtmlEncode(expression[i..(end + 1)]);
                result.Append($"<span class=\"jim-expr-string\">{literal}</span>");
                i = end;
                continue;
            }

            // Numbers
            if (char.IsDigit(c) || (c == '-' && i + 1 < expression.Length && char.IsDigit(expression[i + 1])
                && (i == 0 || !char.IsLetterOrDigit(expression[i - 1]))))
            {
                var numStart = i;
                if (c == '-') i++;
                while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.'))
                    i++;
                var num = WebUtility.HtmlEncode(expression[numStart..i]);
                result.Append($"<span class=\"jim-expr-number\">{num}</span>");
                i--;
                continue;
            }

            // Identifiers, keywords, functions, variables
            if (char.IsLetter(c) || c == '_')
            {
                var idStart = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                    i++;
                var identifier = expression[idStart..i];

                // Look ahead for '(' to detect function calls
                var lookAhead = i;
                while (lookAhead < expression.Length && char.IsWhiteSpace(expression[lookAhead]))
                    lookAhead++;
                var isFunction = lookAhead < expression.Length && expression[lookAhead] == '(';

                var encoded = WebUtility.HtmlEncode(identifier);

                if (identifier is "true" or "false" or "null")
                    result.Append($"<span class=\"jim-expr-keyword\">{encoded}</span>");
                else if (identifier is "mv" or "cs")
                    result.Append($"<span class=\"jim-expr-variable\">{encoded}</span>");
                else if (isFunction)
                    result.Append($"<span class=\"jim-expr-function\">{encoded}</span>");
                else
                    result.Append(encoded);

                i--;
                continue;
            }

            // Multi-character operators
            if (i + 1 < expression.Length)
            {
                var twoChar = expression[i..(i + 2)];
                if (twoChar is "==" or "!=" or ">=" or "<=" or "&&" or "||" or "??")
                {
                    result.Append($"<span class=\"jim-expr-operator\">{WebUtility.HtmlEncode(twoChar)}</span>");
                    i++;
                    continue;
                }
            }

            // Single-character operators
            if (c is '+' or '-' or '*' or '/' or '%' or '>' or '<' or '!' or '?')
            {
                result.Append($"<span class=\"jim-expr-operator\">{WebUtility.HtmlEncode(c.ToString())}</span>");
                continue;
            }

            // Punctuation
            if (c is '(' or ')' or '[' or ']' or ',')
            {
                result.Append($"<span class=\"jim-expr-punctuation\">{c}</span>");
                continue;
            }

            // Whitespace and other characters
            result.Append(WebUtility.HtmlEncode(c.ToString()));
        }

        return result.ToString();
    }

    private static int FindClosingQuote(string text, int openQuoteIndex)
    {
        for (var i = openQuoteIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++; // skip escaped character
                continue;
            }
            if (text[i] == '"')
                return i;
        }
        return text.Length - 1; // unclosed string — return end
    }

    #endregion

    #region Shell Syntax Highlighting

    // Keyword sets are deliberately small; highlighting targets JIM-authored example snippets
    // (curl / Invoke-RestMethod and similar), not arbitrary user scripts.
    private static readonly HashSet<string> BashKeywords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done",
        "case", "esac", "in", "function", "select", "return", "export", "local", "declare", "readonly"
    };

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "elseif", "for", "foreach", "while", "do", "switch", "function", "filter",
        "param", "begin", "process", "end", "try", "catch", "finally", "return", "throw", "break",
        "continue", "in", "trap", "class", "enum", "using"
    };

    /// <summary>
    /// Highlights a bash or PowerShell command snippet with HTML span elements for syntax colouring.
    /// Intended for JIM-authored example snippets, not arbitrary user scripts; it is a pragmatic
    /// tokeniser, not a full shell grammar. Returns an HTML markup string for use with
    /// <c>@((MarkupString)...)</c>. All emitted content is HTML-encoded, so it is safe to render raw.
    /// </summary>
    public static string HighlightShell(string code, ShellLanguage language)
    {
        if (string.IsNullOrEmpty(code))
            return string.Empty;

        var keywords = language == ShellLanguage.PowerShell ? PowerShellKeywords : BashKeywords;
        var result = new StringBuilder();
        var atCommandPosition = true; // the next word is a command (start of snippet, or after a pipe/separator)
        var i = 0;

        while (i < code.Length)
        {
            var c = code[i];

            // Line continuation: a backslash immediately before a newline continues the command,
            // so the next line must NOT be treated as a fresh command position.
            if (c == '\\' && i + 1 < code.Length && code[i + 1] == '\n')
            {
                result.Append("\\\n");
                i += 2;
                continue;
            }

            // Newlines reset command position; other whitespace is emitted verbatim.
            if (c == '\n')
            {
                result.Append('\n');
                atCommandPosition = true;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                result.Append(c);
                i++;
                continue;
            }

            // PowerShell block comment: <# ... #>
            if (language == ShellLanguage.PowerShell && c == '<' && i + 1 < code.Length && code[i + 1] == '#')
            {
                var end = code.IndexOf("#>", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end + 2;
                AppendShellSpan(result, "jim-code-comment", code[i..stop]);
                i = stop;
                atCommandPosition = false;
                continue;
            }

            // Line comment '#': only when it starts a token (line start or after whitespace), so a '#'
            // inside a URL fragment or word is left untouched.
            if (c == '#' && (i == 0 || char.IsWhiteSpace(code[i - 1])))
            {
                var end = code.IndexOf('\n', i);
                var stop = end < 0 ? code.Length : end;
                AppendShellSpan(result, "jim-code-comment", code[i..stop]);
                i = stop;
                continue;
            }

            // Strings (single or double quoted)
            if (c is '"' or '\'')
            {
                var end = FindClosingShellQuote(code, i, c, language);
                AppendShellSpan(result, "jim-code-string", code[i..(end + 1)]);
                i = end + 1;
                atCommandPosition = false;
                continue;
            }

            // Variables: $NAME, ${NAME}, $env:NAME, $_
            if (c == '$')
            {
                var start = i;
                i++;
                if (i < code.Length && code[i] == '{')
                {
                    while (i < code.Length && code[i] != '}') i++;
                    if (i < code.Length) i++; // consume closing brace
                }
                else if (i < code.Length && code[i] == '_')
                {
                    i++; // $_
                }
                else
                {
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == ':')) i++;
                }
                AppendShellSpan(result, "jim-code-variable", code[start..i]);
                atCommandPosition = false;
                continue;
            }

            // Flags / parameters: -x, --long, -Path (only at a token boundary, and followed by a letter or '-')
            if (c == '-' && (i == 0 || char.IsWhiteSpace(code[i - 1])) &&
                i + 1 < code.Length && (char.IsLetter(code[i + 1]) || code[i + 1] == '-'))
            {
                var start = i;
                i++;
                if (i < code.Length && code[i] == '-') i++;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '-' || code[i] == '_')) i++;
                AppendShellSpan(result, "jim-code-flag", code[start..i]);
                atCommandPosition = false;
                continue;
            }

            // Operators / pipes
            if (c is '|' or '&' or ';' or '>' or '<')
            {
                if (i + 1 < code.Length)
                {
                    var two = code.Substring(i, 2);
                    if (two is "&&" or "||" or ">>" or "<<")
                    {
                        AppendShellSpan(result, "jim-code-operator", two);
                        i += 2;
                        atCommandPosition = true;
                        continue;
                    }
                }
                AppendShellSpan(result, "jim-code-operator", c.ToString());
                i++;
                if (c is '|' or ';' or '&') atCommandPosition = true;
                continue;
            }

            // Words: commands, keywords, numbers, or bare arguments (paths, URLs, values)
            if (!IsShellWordBreak(c))
            {
                var start = i;
                while (i < code.Length && !char.IsWhiteSpace(code[i]) && !IsShellWordBreak(code[i]))
                    i++;
                var word = code[start..i];

                if (IsNumericWord(word))
                    AppendShellSpan(result, "jim-code-number", word);
                else if (keywords.Contains(word))
                    AppendShellSpan(result, "jim-code-keyword", word);
                else if (atCommandPosition && LooksLikeCommand(word))
                    AppendShellSpan(result, "jim-code-command", word);
                else
                    result.Append(WebUtility.HtmlEncode(word));

                atCommandPosition = false;
                continue;
            }

            // Any other single character: emit encoded.
            result.Append(WebUtility.HtmlEncode(c.ToString()));
            i++;
        }

        return result.ToString();
    }

    private static void AppendShellSpan(StringBuilder builder, string cssClass, string content) =>
        builder.Append($"<span class=\"{cssClass}\">{WebUtility.HtmlEncode(content)}</span>");

    // Characters that end a bare word and (except whitespace) begin a distinct token type.
    // '#' and '-' are intentionally excluded: they are context-sensitive (comment/flag only at a
    // token boundary) and are valid mid-word (e.g. URL fragments, Verb-Noun cmdlets).
    private static bool IsShellWordBreak(char c) =>
        c is '"' or '\'' or '$' or '|' or '&' or ';' or '<' or '>';

    private static bool LooksLikeCommand(string word) =>
        word.Length > 0 && (char.IsLetter(word[0]) || word[0] is '_' or '.' or '/');

    private static bool IsNumericWord(string word)
    {
        if (word.Length == 0)
            return false;

        var seenDot = false;
        foreach (var ch in word)
        {
            if (ch == '.')
            {
                if (seenDot) return false;
                seenDot = true;
            }
            else if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return word != ".";
    }

    private static int FindClosingShellQuote(string text, int openIndex, char quote, ShellLanguage language)
    {
        // Single quotes are literal in both shells (no escapes). Double quotes escape with a
        // backslash in bash and a backtick in PowerShell.
        var escape = quote == '"' ? (language == ShellLanguage.PowerShell ? '`' : '\\') : '\0';
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (escape != '\0' && text[i] == escape && i + 1 < text.Length)
            {
                i++; // skip the escaped character
                continue;
            }
            if (text[i] == quote)
                return i;
        }
        return text.Length - 1; // unclosed — extend to end of input
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
    /// Gets a MudBlazor colour for the sync outcome type chip. Delegates to
    /// <see cref="OutcomeDisplayMap"/>, the single source of truth for outcome display mappings.
    /// </summary>
    public static Color GetOutcomeTypeMudBlazorColor(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return OutcomeDisplayMap.ToMudBlazorColor(OutcomeDisplayMap.Get(outcomeType).Tone);
    }

    /// <summary>
    /// Gets the technical display name for a sync outcome type (e.g. "MVO Projected"). Delegates to
    /// <see cref="OutcomeDisplayMap"/>, the single source of truth for outcome display mappings.
    /// </summary>
    public static string GetOutcomeTypeDisplayName(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return OutcomeDisplayMap.Get(outcomeType).TechnicalLabel;
    }

    /// <summary>
    /// Gets a Material icon string for a sync outcome type. Delegates to
    /// <see cref="OutcomeDisplayMap"/>, the single source of truth for outcome display mappings.
    /// </summary>
    public static string GetOutcomeTypeIcon(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return OutcomeDisplayMap.Get(outcomeType).Icon;
    }

    /// <summary>
    /// Parses the denormalised OutcomeSummary string into outcome type/count pairs.
    /// Format: "Projected:1,AttributeFlow:12,PendingExportCreated:2"
    /// Returns empty list if null, empty, or unparseable.
    /// </summary>
    public static List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, int Count)> ParseOutcomeSummary(string? outcomeSummary)
    {
        var results = new List<(ActivityRunProfileExecutionItemSyncOutcomeType, int)>();
        if (string.IsNullOrWhiteSpace(outcomeSummary))
            return results;

        foreach (var part in outcomeSummary.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = part.LastIndexOf(':');
            if (colonIndex <= 0 || colonIndex >= part.Length - 1)
                continue;

            var typeName = part[..colonIndex];
            var countStr = part[(colonIndex + 1)..];

            if (Enum.TryParse<ActivityRunProfileExecutionItemSyncOutcomeType>(typeName, out var outcomeType)
                && int.TryParse(countStr, out var count)
                && count > 0)
            {
                results.Add((outcomeType, count));
            }
        }

        return results;
    }
    #endregion
}