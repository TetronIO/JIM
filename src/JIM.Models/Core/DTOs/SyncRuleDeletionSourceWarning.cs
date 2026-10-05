// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// What saving a Synchronisation Rule is about to do to a Metaverse Object Type's deletion behaviour (#1256): the rule
/// will project objects into a type deleted When Authoritative Source Disconnected, from a Connected System that is not
/// one of its authoritative sources. The parts are kept separate so the portal can lay them out; <see cref="Message"/>
/// is the same content as one piece of text for REST and PowerShell.
/// </summary>
/// <param name="ConnectedSystemName">The Connected System the rule projects from.</param>
/// <param name="ObjectTypeName">The Metaverse Object Type it projects into.</param>
/// <param name="AuthoritativeSourceNames">The type's selected authoritative sources, by name.</param>
public sealed record SyncRuleDeletionSourceWarning(
    string ConnectedSystemName,
    string ObjectTypeName,
    IReadOnlyList<string> AuthoritativeSourceNames)
{
    /// <summary>One line naming the problem.</summary>
    public string Headline => $"{ConnectedSystemName} will project {ObjectTypeName} objects it can never delete";

    /// <summary>The deletion settings in force, leading into the two consequences.</summary>
    public string Explanation => AuthoritativeSourceNames.Count == 1
        ? $"A {ObjectTypeName} object in the Metaverse is deleted When Authoritative Source Disconnected, and its " +
          $"authoritative source is {AuthoritativeSourceNames[0]} only. Until {ConnectedSystemName} is added, or " +
          "projection is turned off:"
        : $"A {ObjectTypeName} object in the Metaverse is deleted When Authoritative Source Disconnected, and its " +
          $"authoritative sources are {JoinNames(AuthoritativeSourceNames)} only. Until {ConnectedSystemName} is " +
          "added, or projection is turned off:";

    /// <summary>The consequence for objects only the projecting system holds.</summary>
    public string NeverDeletedConsequence =>
        $"a {ObjectTypeName} projected by {ConnectedSystemName} that no selected source also holds will never be deleted";

    /// <summary>The consequence for objects the projecting system shares with a selected source.</summary>
    public string DeletedWhileHeldConsequence => AuthoritativeSourceNames.Count == 1
        ? $"a {ObjectTypeName} that leaves {AuthoritativeSourceNames[0]} is deleted even if {ConnectedSystemName} still holds it"
        : $"a {ObjectTypeName} that leaves its authoritative sources is deleted even if {ConnectedSystemName} still holds it";

    /// <summary>The whole warning as one piece of text, for REST responses and PowerShell warnings.</summary>
    public string Message => $"{Headline}. {Explanation} {NeverDeletedConsequence}; and {DeletedWhileHeldConsequence}.";

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };
}
