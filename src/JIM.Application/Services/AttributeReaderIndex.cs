// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;
using JIM.Models.Core.DTOs;
using JIM.Models.Expressions;

namespace JIM.Application.Services;

/// <summary>
/// The configuration that reads each attribute (#2265): Scoping Criteria comparing it directly or reading it in an
/// Expression, and the Expressions of Attribute Flows, Object Matching Rules and Example Data Templates. Deleting,
/// unassigning or renaming an attribute any of these reads would leave the reader evaluating against nothing, which
/// for a Scoping Criterion takes objects out of scope; callers refuse the change and name every reader instead.
/// </summary>
/// <remarks>
/// Built once from the flattened candidates and queried per attribute, so a schema refresh removing many attributes
/// resolves each Expression once. An Expression reads an attribute by name, which ignores case (#2051), and only on
/// the side its accessor names (<c>mv</c> or <c>cs</c>); a reader only counts against the Object Type it evaluates
/// against, so a Connected System attribute of the same name on another Object Type is not one it reads.
/// </remarks>
public sealed class AttributeReaderIndex
{
    private readonly List<AttributeReaderCandidate> _directMetaverse = [];
    private readonly List<AttributeReaderCandidate> _directConnectedSystem = [];
    private readonly Dictionary<(ExpressionInputSource Side, string Name), List<AttributeReaderCandidate>> _byExpressionInput = new();

    /// <summary>
    /// Indexes the candidates. Candidates with neither a direct attribute nor an Expression are ignored.
    /// </summary>
    public AttributeReaderIndex(IEnumerable<AttributeReaderCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        foreach (var candidate in candidates)
        {
            if (candidate.MetaverseAttributeId.HasValue)
                _directMetaverse.Add(candidate);
            if (candidate.ConnectedSystemAttributeId.HasValue)
                _directConnectedSystem.Add(candidate);

            foreach (var input in ExpressionInputResolver.ResolveCached(candidate.Expression))
            {
                var key = (input.Source, input.AttributeName.ToLowerInvariant());
                if (!_byExpressionInput.TryGetValue(key, out var readers))
                    _byExpressionInput[key] = readers = [];
                readers.Add(candidate);
            }
        }
    }

    /// <summary>
    /// Returns what reads a Metaverse Attribute.
    /// </summary>
    /// <param name="attributeId">The attribute's id, matched against Scoping Criteria comparing it directly.</param>
    /// <param name="attributeName">The attribute's name, matched against <c>mv["..."]</c> inputs ignoring case.</param>
    /// <param name="metaverseObjectTypeId">Only readers evaluating against this Metaverse Object Type; null for all.</param>
    /// <param name="expressionsOnly">Only readers naming the attribute in an Expression: what a rename breaks.</param>
    public IReadOnlyList<AttributeReader> ForMetaverseAttribute(int attributeId, string attributeName, int? metaverseObjectTypeId = null, bool expressionsOnly = false)
    {
        var direct = expressionsOnly
            ? []
            : _directMetaverse.Where(c => c.MetaverseAttributeId == attributeId);
        var viaExpression = ExpressionReaders(ExpressionInputSource.Metaverse, attributeName);

        return Collect(direct, viaExpression, c => metaverseObjectTypeId == null || c.MetaverseObjectTypeId == metaverseObjectTypeId);
    }

    /// <summary>
    /// Returns what reads a Connected System attribute of one Connected System Object Type.
    /// </summary>
    /// <param name="attributeId">The attribute's id, matched against Scoping Criteria comparing it directly.</param>
    /// <param name="attributeName">The attribute's name, matched against <c>cs["..."]</c> inputs ignoring case.</param>
    /// <param name="connectedSystemObjectTypeId">The Object Type the attribute belongs to.</param>
    /// <param name="expressionsOnly">Only readers naming the attribute in an Expression.</param>
    public IReadOnlyList<AttributeReader> ForConnectedSystemAttribute(int attributeId, string attributeName, int connectedSystemObjectTypeId, bool expressionsOnly = false)
    {
        var direct = expressionsOnly
            ? []
            : _directConnectedSystem.Where(c => c.ConnectedSystemAttributeId == attributeId);
        var viaExpression = ExpressionReaders(ExpressionInputSource.ConnectedSystem, attributeName);

        return Collect(direct, viaExpression, c => c.ConnectedSystemObjectTypeId == connectedSystemObjectTypeId);
    }

    private IEnumerable<AttributeReaderCandidate> ExpressionReaders(ExpressionInputSource side, string attributeName) =>
        _byExpressionInput.TryGetValue((side, attributeName.ToLowerInvariant()), out var readers) ? readers : [];

    private static List<AttributeReader> Collect(
        IEnumerable<AttributeReaderCandidate> direct,
        IEnumerable<AttributeReaderCandidate> viaExpression,
        Func<AttributeReaderCandidate, bool> inScope)
    {
        var readers = new List<AttributeReader>();
        var seen = new HashSet<(AttributeReaderKind, int)>();

        // A criterion both comparing the attribute and reading it cannot happen (a criterion is one or the other),
        // but an item is still listed once whichever way it reads the attribute.
        readers.AddRange(direct.Where(inScope).Where(c => seen.Add((c.Kind, c.ItemId))).Select(c => ToReader(c, viaExpression: false)));
        readers.AddRange(viaExpression.Where(inScope).Where(c => seen.Add((c.Kind, c.ItemId))).Select(c => ToReader(c, viaExpression: true)));

        return readers;
    }

    private static AttributeReader ToReader(AttributeReaderCandidate candidate, bool viaExpression) => new()
    {
        Kind = candidate.Kind,
        ItemId = candidate.ItemId,
        ViaExpression = viaExpression,
        Expression = viaExpression ? candidate.Expression : null,
        SyncRuleId = candidate.SyncRuleId,
        SyncRuleName = candidate.SyncRuleName,
        ExampleDataTemplateId = candidate.ExampleDataTemplateId,
        ExampleDataTemplateName = candidate.ExampleDataTemplateName,
        Description = Describe(candidate, viaExpression).ToEnglish(),
        DescriptionDetail = Describe(candidate, viaExpression)
    };

    private static JimMessage Describe(AttributeReaderCandidate candidate, bool viaExpression)
    {
        var owner = candidate.SyncRuleName != null
            ? ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderOwnerSyncRule), candidate.SyncRuleName)
            : candidate.ExampleDataTemplateName != null
                ? ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderOwnerExampleDataTemplate), candidate.ExampleDataTemplateName)
                : ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderOwnerObjectType), candidate.ConnectedSystemObjectTypeName);

        return candidate.Kind switch
        {
            AttributeReaderKind.ScopingCriterion when viaExpression =>
                ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderScopingCriterionExpression), candidate.Expression, owner),
            AttributeReaderKind.ScopingCriterion =>
                ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderScopingCriterionAttribute), candidate.AttributeName, owner),
            AttributeReaderKind.AttributeFlowExpression =>
                ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderAttributeFlowExpression), candidate.TargetName, owner),
            AttributeReaderKind.ObjectMatchingRuleExpression =>
                ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderObjectMatchingRuleExpression), candidate.Expression, owner),
            AttributeReaderKind.ExampleDataTemplateExpression =>
                ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderExampleDataExpression), candidate.TargetName, owner),
            _ => ConnectedSystemMessage.Create(nameof(ConnectedSystemMessages.ReaderOther), candidate.Kind.ToString(), owner)
        };
    }
}
