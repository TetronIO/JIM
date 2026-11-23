// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;

namespace JIM.Application.Servers;

/// <summary>
/// Decides whether an expression scoping criterion can ever be evaluated, before its Synchronisation Rule is saved
/// (#2054).
/// </summary>
/// <remarks>
/// Synchronisation stops an object whose scope a criterion cannot decide, which is the right outcome for one object's
/// odd value and the wrong place to learn that the expression is broken for every object. So every surface that saves a
/// rule refuses it here first, with a reason. What this passes fails only on a particular object's data: a value that
/// cannot be compared as the chosen type, or an expression that throws on one input. The operator, and the value it
/// compares against, are checked against the Compare As type by the shared criterion validation.
/// </remarks>
public sealed class ScopingCriterionExpressionValidator
{
    /// <summary>
    /// The types an expression's computed value can be compared as, in the order a picker offers them. Reference and
    /// Binary are left out: no scoping operator compares a computed value of either meaningfully.
    /// </summary>
    public static readonly IReadOnlyList<AttributeDataType> CompareAsTypes =
    [
        AttributeDataType.Text, AttributeDataType.Number, AttributeDataType.LongNumber, AttributeDataType.Decimal,
        AttributeDataType.DateTime, AttributeDataType.Boolean, AttributeDataType.Guid
    ];

    private readonly IExpressionEvaluator _expressionEvaluator;

    public ScopingCriterionExpressionValidator(IExpressionEvaluator expressionEvaluator)
    {
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
    }

    /// <summary>
    /// Why <paramref name="criterion"/> could never be evaluated for objects of the scoped type, or null when it can (an
    /// attribute criterion is always null here).
    /// </summary>
    /// <param name="criterion">The criterion.</param>
    /// <param name="direction">The direction of its Synchronisation Rule, which decides the side it reads.</param>
    /// <param name="attributeNames">The names of the scoped object type's attributes.</param>
    /// <param name="objectTypeName">The scoped object type's name, for the message.</param>
    public string? DescribeProblem(SyncRuleScopingCriteria criterion, SyncRuleDirection direction,
        IReadOnlyCollection<string> attributeNames, string objectTypeName) =>
        DescribeProblemDetail(criterion, direction, attributeNames, objectTypeName)?.ToEnglish();

    /// <summary>
    /// <see cref="DescribeProblem"/> as a message, for the portal to show in the viewer's language (#3030); its
    /// British English is what <see cref="DescribeProblem"/> returns, so the REST API and logs read what they always did.
    /// </summary>
    public JimMessage? DescribeProblemDetail(SyncRuleScopingCriteria criterion, SyncRuleDirection direction,
        IReadOnlyCollection<string> attributeNames, string objectTypeName)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        ArgumentNullException.ThrowIfNull(attributeNames);

        if (!criterion.IsExpression)
            return null;

        var expression = criterion.Expression!;
        if (criterion.MetaverseAttributeId.HasValue || criterion.MetaverseAttribute != null ||
            criterion.ConnectedSystemAttributeId.HasValue || criterion.ConnectedSystemAttribute != null)
            return JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionBothAttributeAndExpression));

        if (criterion.CompareAs is not { } compareAs || !CompareAsTypes.Contains(compareAs))
            return JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionNeedsCompareAs));

        if (expression.Length > DynamicExpressoEvaluator.MaxExpressionLength)
            return JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionTooLong), expression.Length, DynamicExpressoEvaluator.MaxExpressionLength);

        var parsed = _expressionEvaluator.Validate(expression);
        if (!parsed.IsValid)
            return JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionDoesNotParse), parsed.ErrorMessage);

        var exporting = direction == SyncRuleDirection.Export;
        var scopedSide = exporting ? ExpressionInputSource.Metaverse : ExpressionInputSource.ConnectedSystem;
        var inputs = ExpressionInputResolver.Resolve(expression);

        var otherSideInput = inputs.FirstOrDefault(input => input.Source != scopedSide);
        if (otherSideInput != null)
            return exporting
                ? JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionReadsConnectedSystemOnExport), otherSideInput.Accessor)
                : JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionReadsMetaverseOnImport), otherSideInput.Accessor);

        var scopedInputs = inputs.Where(input => input.Source == scopedSide).ToList();
        if (scopedInputs.Count == 0)
            return exporting
                ? JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionReadsNoAttributeOnExport))
                : JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionReadsNoAttributeOnImport));

        // Inputs are looked up ignoring case when the expression runs, so they are here too.
        var unknown = scopedInputs.FirstOrDefault(input =>
            !attributeNames.Any(name => string.Equals(name, input.AttributeName, StringComparison.OrdinalIgnoreCase)));
        if (unknown != null)
            return JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionUnknownAttribute), unknown.Accessor, objectTypeName);

        // An expression that reads the clock would put objects in and out of scope with nothing to notice it: no
        // change arrives, and temporal reconciliation only revisits criteria compared against a relative date.
        var nonRepeatable = NonRepeatableFunctionDetector.Find(expression);
        if (nonRepeatable.Count > 0)
            return JimMessage.Create(ApplicationMessageCatalogues.Scoping, nameof(ScopingMessages.ExpressionNonRepeatable), string.Join(", ", nonRepeatable));

        return null;
    }
}
