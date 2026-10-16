// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A change a schema refresh applies to how the Connected System classifies a selected Object Type JIM already
/// holds: its class kind (structural, auxiliary, abstract), or the classes it inherits from where that involves
/// another selected Object Type. Either can change which Object Type an existing entry resolves to at the next
/// import, so the administrator must see it before applying (#2049); by default an object that would change Object
/// Type is refused at import and kept as it is, and a Full Import Run Profile with Allow Object Type Changes enabled
/// carries the change out.
/// </summary>
public class SchemaObjectTypeClassificationChange
{
    /// <summary>
    /// Which aspect of the classification changed.
    /// </summary>
    public required SchemaObjectTypeClassificationAspect Aspect { get; set; }

    /// <summary>
    /// The values JIM held before the refresh, sorted: the class kinds for <see cref="SchemaObjectTypeClassificationAspect.ClassKind"/>,
    /// or every class the Object Type inherited from for <see cref="SchemaObjectTypeClassificationAspect.Inheritance"/>. Empty when
    /// the Object Type carried none.
    /// </summary>
    public List<string> OldValues { get; set; } = new();

    /// <summary>
    /// The values the Connector reported, sorted, in the same terms as <see cref="OldValues"/>. Empty when the Connector reported
    /// none.
    /// </summary>
    public List<string> NewValues { get; set; } = new();
}

/// <summary>
/// The aspect of an Object Type's classification a schema refresh changed.
/// </summary>
public enum SchemaObjectTypeClassificationAspect
{
    /// <summary>
    /// The Object Type's class kind (structural, auxiliary or abstract) was restated by the Connector. A structural
    /// class takes entries from an auxiliary one, so a change here can move entries between Object Types.
    /// </summary>
    ClassKind = 0,

    /// <summary>
    /// The classes the Object Type inherits from gained or lost another selected Object Type. The more specific of
    /// two structural classes takes the entries carrying both, so a change here can move entries between them.
    /// </summary>
    Inheritance = 1
}
