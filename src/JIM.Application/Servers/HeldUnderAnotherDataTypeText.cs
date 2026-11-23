// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Localisation;

namespace JIM.Application.Servers;

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

    /// <summary>
    /// <see cref="Describe(string, AttributeDataType, AttributeDataType?, string)"/> as a message the portal shows in the
    /// viewer's language (#3030), with the same British English.
    /// </summary>
    /// <param name="subject">What reads the value, as the start of a sentence: <see cref="AttributeSubject"/> or <see cref="InputsSubjectMessage"/>.</param>
    /// <param name="heldType">The data type the value is held as.</param>
    /// <param name="currentType">The attribute's current data type, when the subject is one attribute; null otherwise.</param>
    /// <param name="afterImport">What then happens, as the end of a sentence: <see cref="AfterImportMatched"/> or <see cref="AfterImportScope"/>.</param>
    internal static JimMessage DescribeMessage(JimMessage subject, AttributeDataType heldType, AttributeDataType? currentType, JimMessage afterImport)
    {
        MessageArgument current = currentType.HasValue
            ? ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.HeldNotAsCurrentType), currentType.Value.ToString())
            : string.Empty;
        return ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.HeldUnderAnotherDataType), subject, heldType.ToString(), current, afterImport);
    }

    /// <summary>The subject for a Connected System attribute: "The Connected System attribute 'mail'".</summary>
    internal static JimMessage AttributeSubject(string attributeName) =>
        ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.HeldSubjectAttribute), attributeName);

    /// <summary>What then happens, for a value matched on the next synchronisation.</summary>
    internal static JimMessage AfterImportMatched =>
        ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.HeldAfterImportMatched));

    /// <summary>What then happens, for an object whose scope is decided at the next synchronisation.</summary>
    internal static JimMessage AfterImportScope =>
        ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.HeldAfterImportScope));

    /// <summary>The subject for an Expression's inputs held only under another data type: one, or several.</summary>
    internal static string InputsSubject(IReadOnlyList<HeldExpressionInput> heldInputs) =>
        InputsSubjectMessage(heldInputs).ToEnglish();

    /// <summary><see cref="InputsSubject"/> as a message.</summary>
    internal static JimMessage InputsSubjectMessage(IReadOnlyList<HeldExpressionInput> heldInputs) => heldInputs.Count == 1
        ? ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.HeldSubjectInput), heldInputs[0].Accessor)
        : ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.HeldSubjectInputs), string.Join(", ", heldInputs));
}
