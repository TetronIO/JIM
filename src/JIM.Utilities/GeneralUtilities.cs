using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using System.Text.RegularExpressions;
namespace JIM.Utilities;

public static class Utilities
{
    public static string Pluralise(this string word)
    {
        if (string.IsNullOrEmpty(word))
            return word;

        if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("z", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
            return word + "es";

        if (word.EndsWith("y", StringComparison.OrdinalIgnoreCase) &&
            word.Length > 1 &&
            !"aeiou".Contains(word[^2], StringComparison.OrdinalIgnoreCase))
            return word[..^1] + "ies";

        return word + "s";
    }

    public static string SplitOnCapitalLetters(this string inputString)
    {
        if (string.IsNullOrEmpty(inputString))
            return inputString;

        var words = Regex.Matches(inputString, @"([A-Z][a-z]+)").Cast<Match>().Select(m => m.Value).ToList();

        // If no matches (e.g., all lowercase like "person"), return the original string
        if (words.Count == 0)
            return inputString;

        return string.Join(" ", words);
    }
        
    public static bool AreByteArraysTheSame(ReadOnlySpan<byte> array1, ReadOnlySpan<byte> array2)
    {
        // byte[] is implicitly convertible to ReadOnlySpan<byte>
        return array1.SequenceEqual(array2);
    }

    public static bool AreByteArraysTheSame(byte[]? array1, byte[]? array2)
    {
        if (array1 == null && array2 == null) return true;
        if (array1 == null || array2 == null) return false;
        return AreByteArraysTheSame((ReadOnlySpan<byte>)array1, (ReadOnlySpan<byte>)array2);
    }

    public static string GetMetaverseObjectHref(MetaverseObject metaverseObject)
    {
        return $"/t/{metaverseObject.Type.PluralName.ToLower()}/v/{metaverseObject.Id}";
    }
    public static string? GetMetaverseObjectHrefText(MetaverseObject metaverseObject)
    {
        return metaverseObject.GetAttributeValue(Constants.BuiltInAttributes.DisplayName)?.StringValue;
    }

    public static string GetMetaverseObjectHref(MetaverseObjectHeader metaverseObjectHeader)
    {
        return $"/t/{metaverseObjectHeader.TypePluralName.ToLower()}/v/{metaverseObjectHeader.Id}";
    }

    public static string? GetMetaverseObjectHrefText(MetaverseObjectHeader metaverseObjectHeader)
    {
        return metaverseObjectHeader.GetAttributeValue(Constants.BuiltInAttributes.DisplayName)?.StringValue;
    }

    public static string GetConnectedSystemHref(ConnectedSystem connectedSystem)
    {
        return $"/admin/connected-systems/{connectedSystem.Id}";
    }

    public static string GetConnectedSystemHref(ConnectedSystemHeader connectedSystemHeader)
    {
        return $"/admin/connected-systems/{connectedSystemHeader.Id}";
    }

    public static string GetConnectedSystemObjectsHref(ConnectedSystemHeader connectedSystemHeader)
    {
        return GetConnectedSystemObjectsHref(connectedSystemHeader.Id);
    }

    public static string GetConnectedSystemObjectsHref(int connectedSystemId)
    {
        return $"/admin/connected-systems/{connectedSystemId}/objects";
    }

    public static string GetConnectedSystemObjectHref(ConnectedSystemObject connectedSystemObject)
    {
        return GetConnectedSystemObjectHref(connectedSystemObject.ConnectedSystemId, connectedSystemObject.Id);
    }

    public static string GetConnectedSystemObjectHref(ConnectedSystemObjectHeader connectedSystemObjectHeader)
    {
        return GetConnectedSystemObjectHref(connectedSystemObjectHeader.ConnectedSystemId, connectedSystemObjectHeader.Id);
    }

    public static string GetConnectedSystemObjectHref(int connectedSystemId, Guid connectedSystemObjectId)
    {
        return $"/admin/connected-systems/{connectedSystemId}/objects/{connectedSystemObjectId}";
    }

    /// <summary>
    /// Returns the href for the API Keys list page.
    /// </summary>
    public static string GetApiKeysHref()
    {
        return "/admin/apikeys";
    }

    /// <summary>
    /// Returns the href for a specific API Key detail page.
    /// </summary>
    public static string GetApiKeyHref(Guid apiKeyId)
    {
        return $"/admin/apikeys/{apiKeyId}";
    }

    /// <summary>
    /// Checks if an InitiatedByName string represents an API Key.
    /// Returns the API key name without the prefix if it's an API key, otherwise returns null.
    /// </summary>
    public static string? ExtractApiKeyName(string? initiatedByName)
    {
        const string apiKeyPrefix = "API Key: ";
        if (string.IsNullOrEmpty(initiatedByName))
            return null;

        if (initiatedByName.StartsWith(apiKeyPrefix, StringComparison.Ordinal))
            return initiatedByName.Substring(apiKeyPrefix.Length);

        return null;
    }
}