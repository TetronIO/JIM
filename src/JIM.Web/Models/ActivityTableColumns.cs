// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The column widths shared by the Activity tab's three sections (#2549), so Queued, In progress and Completed line
/// up and the eye can track a column down the page. One place, because the sections are separate tables: Queued and
/// In progress are plain tables, Completed is a virtualised grid.
/// </summary>
/// <remarks>
/// The columns are, in order: the row's state (run position, Processing, Result), Connected System, Run Profile, the
/// detail (status, progress, outcomes; it takes the remaining width), Initiated By, two time columns and the actions.
/// </remarks>
public static class ActivityTableColumns
{
    public const string State = "width: 150px;";
    public const string ConnectedSystem = "width: 15%;";
    public const string RunProfile = "width: 14%;";
    public const string Detail = "";
    public const string InitiatedBy = "width: 15%;";
    public const string FirstTime = "width: 130px;";
    public const string SecondTime = "width: 120px;";
    public const string Actions = "width: 64px;";
}
