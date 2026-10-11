// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A change a schema refresh applies to how the Connected System classifies a selected Object Type. It can change
/// which Object Type existing entries resolve to at the next import: by default each object that would change
/// Object Type is refused there and reported as an Object Type Changed error, and a Full Import Run Profile with
/// <c>allowObjectTypeChanges</c> enabled carries the change out.
/// </summary>
public class SchemaObjectTypeClassificationChangeDto
{
    /// <summary>
    /// Which aspect changed: "ClassKind" (structural, auxiliary or abstract) or "Inheritance" (the classes the Object
    /// Type inherits from gained or lost another selected Object Type).
    /// </summary>
    public string Aspect { get; set; } = null!;

    /// <summary>
    /// The values JIM held before the refresh, sorted: the class kinds, or every class the Object Type inherited
    /// from. Empty when it carried none.
    /// </summary>
    public List<string> OldValues { get; set; } = new();

    /// <summary>
    /// The values the Connector reported, sorted, in the same terms as <see cref="OldValues"/>. Empty when it reported
    /// none.
    /// </summary>
    public List<string> NewValues { get; set; } = new();

    /// <summary>
    /// Maps a classification change to its API representation.
    /// </summary>
    public static SchemaObjectTypeClassificationChangeDto FromModel(SchemaObjectTypeClassificationChange change)
    {
        return new SchemaObjectTypeClassificationChangeDto
        {
            Aspect = change.Aspect.ToString(),
            OldValues = change.OldValues,
            NewValues = change.NewValues
        };
    }
}
