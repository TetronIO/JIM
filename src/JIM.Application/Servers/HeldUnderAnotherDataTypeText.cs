// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;

namespace JIM.Application.Servers;

/// <summary>
/// The wording every reader that stops on a value held only under another data type uses (#2319, #2329), so Object
/// Matching and scoping explain the same situation in the same words. The rule itself is
/// <see cref="SyncEngine.HeldOnlyUnderAnotherDataType"/>.
/// </summary>
internal static class HeldUnderAnotherDataTypeText
{
    /// <summary>
    /// Why something reading a value held only under another data type cannot be worked out, and what resolves it.
    /// </summary>
    /// <param name="subject">What reads the value, as the start of a sentence, for example "The Connected System attribute 'mail'".</param>
    /// <param name="heldType">The data type the value is held as.</param>
    /// <param name="currentType">The attribute's current data type, when the subject is one attribute; null otherwise.</param>
    /// <param name="afterImport">What then happens, as the end of a sentence, for example "the object is matched on the next synchronisation".</param>
    internal static string Describe(string subject, AttributeDataType heldType, AttributeDataType? currentType, string afterImport)
    {
        var current = currentType.HasValue ? $", not as its {currentType.Value} type" : string.Empty;
        return $"{subject} is held as {heldType}{current}: a schema refresh changed its data type and no import has read it since. " +
               $"Run a Full Import to read it as its new type; {afterImport}.";
    }

    /// <summary>The subject for an Expression's inputs held only under another data type: one, or several.</summary>
    internal static string InputsSubject(IReadOnlyList<HeldExpressionInput> heldInputs) =>
        heldInputs.Count == 1 ? $"The input {heldInputs[0].Accessor}" : $"The inputs {string.Join(", ", heldInputs)}";
}
