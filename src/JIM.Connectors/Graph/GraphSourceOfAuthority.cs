// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;

namespace JIM.Connectors.Graph;

/// <summary>
/// Derives <c>sourceOfAuthority</c>: where a user or group is managed, which Graph holds no single property for. It
/// tells an administrator which objects JIM can write to, and is what export Synchronisation Rules scope on.
/// </summary>
/// <remarks>
/// A guest is external whatever else is true of it, so that rule is checked first. A synchronised object whose Source of
/// Authority was switched to Entra ID still reads <c>onPremisesSyncEnabled</c> as true, so the import passes in whether
/// the object is among those Graph reports as cloud-managed; it knows only while Track Source of Authority Changes is on.
/// </remarks>
internal static class GraphSourceOfAuthority
{
    public const string Cloud = "cloud";
    public const string OnPremisesDirectory = "onPremisesDirectory";
    public const string ExternalEntraDirectory = "externalEntraDirectory";

    /// <summary>The properties the rules read, always selected whatever the administrator chose to import.</summary>
    public static readonly string[] UserProperties = ["onPremisesSyncEnabled", "userType", "creationType", "externalUserState"];

    public static readonly string[] GroupProperties = ["onPremisesSyncEnabled"];

    public static string Derive(JsonElement graphObject, bool isUser, bool isCloudManaged)
    {
        if (isUser && IsExternal(graphObject))
            return ExternalEntraDirectory;

        return !isCloudManaged && IsTrue(graphObject, "onPremisesSyncEnabled")
            ? OnPremisesDirectory
            : Cloud;
    }

    /// <summary>
    /// A guest (<c>userType</c> Guest), or an account invited from another organisation even where it was made a
    /// member: an invitation leaves <c>creationType</c> as Invitation or an <c>externalUserState</c>.
    /// </summary>
    private static bool IsExternal(JsonElement user) =>
        string.Equals(ReadString(user, "userType"), "Guest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ReadString(user, "creationType"), "Invitation", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(ReadString(user, "externalUserState"));

    private static bool IsTrue(JsonElement graphObject, string property) =>
        graphObject.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement graphObject, string property) =>
        graphObject.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
