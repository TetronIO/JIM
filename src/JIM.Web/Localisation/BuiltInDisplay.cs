// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Web.Resources;

namespace JIM.Web.Localisation;

/// <summary>
/// The names and descriptions of JIM's built-in Schedules and Roles as a person reads them, in their language (#3030).
/// A built-in is looked up by its stable name in <c>Resources/BuiltInText.resx</c>; a Schedule or Role an administrator
/// created is shown exactly as stored, because JIM has no translation for a name a person chose.
/// </summary>
public static class BuiltInDisplay
{
    /// <summary>The Schedule's name in the viewer's language when it is built in; the stored name otherwise.</summary>
    public static string ScheduleName(string storedName, bool builtIn) =>
        builtIn
            ? BuiltInText.ResourceManager.GetString(ScheduleKey(storedName), CultureInfo.CurrentUICulture) ?? storedName
            : storedName;

    /// <summary>The Schedule's description in the viewer's language when it is built in; the stored description otherwise.</summary>
    public static string? ScheduleDescription(string storedName, bool builtIn, string? storedDescription) =>
        builtIn
            ? BuiltInText.ResourceManager.GetString(ScheduleKey(storedName) + "_Description", CultureInfo.CurrentUICulture) ?? storedDescription
            : storedDescription;

    /// <summary>The Role's name in the viewer's language when it is built in; the stored name otherwise.</summary>
    public static string RoleName(string storedName, bool builtIn) =>
        builtIn
            ? BuiltInText.ResourceManager.GetString("Role_" + RemoveSpaces(storedName), CultureInfo.CurrentUICulture) ?? storedName
            : storedName;

    /// <summary>The resource key for a built-in Schedule, from its stable name with spaces removed.</summary>
    public static string ScheduleKey(string stableName) => "Schedule_" + RemoveSpaces(stableName);

    private static string RemoveSpaces(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal);
}
