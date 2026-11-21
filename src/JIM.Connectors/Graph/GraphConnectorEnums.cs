// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Graph;

/// <summary>What a <see cref="GraphExportStep"/> does, which decides how its answer is read.</summary>
internal enum GraphExportStepKind
{
    /// <summary><c>POST /users</c> or <c>POST /groups</c>.</summary>
    Create,

    /// <summary><c>PATCH</c> of the object's properties.</summary>
    Update,

    /// <summary><c>DELETE</c> of the object; one already gone counts as deleted.</summary>
    Delete,

    /// <summary><c>PATCH</c> of a user's <c>accountEnabled</c> to false, in place of a delete; one already gone counts as done.</summary>
    Disable,

    /// <summary><c>PUT /users/{id}/manager/$ref</c>.</summary>
    SetManager,

    /// <summary><c>DELETE /users/{id}/manager/$ref</c>; a user with no manager counts as done.</summary>
    RemoveManager,

    /// <summary>
    /// Adds members or owners: up to twenty members in one <c>PATCH</c> through <c>members@odata.bind</c>, or one member
    /// or owner through <c>POST .../$ref</c>. One already present counts as added.
    /// </summary>
    AddReferences,

    /// <summary><c>DELETE /groups/{id}/{relationship}/{reference}/$ref</c>; one already gone counts as removed.</summary>
    RemoveReference
}
