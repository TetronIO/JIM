// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// #2817: what a Delta Import asks the database about one multi-valued reference attribute of a Connected System Object it
/// is about to compare with an imported object, instead of loading every value the attribute holds: which of the held
/// values none of the imported values names, and which imported values no held value names. A group of 450,000 members
/// that lost a few of them is then answered with those few, not with every member.
/// </summary>
/// <param name="ConnectedSystemObjectId">The object JIM holds.</param>
/// <param name="AttributeId">The multi-valued reference attribute asked about.</param>
/// <param name="Values">
/// Every value the imported object carries for the attribute, exactly as the comparison will read them: distinct ignoring
/// case, in the Connector's order. The comparison refuses an answer given for any other list.
/// </param>
public sealed record HeldReferenceValueQuestion(Guid ConnectedSystemObjectId, int AttributeId, IReadOnlyList<string> Values);
