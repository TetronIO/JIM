// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Expressions;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a SyncRuleMapping.
/// </summary>
public class SyncRuleMappingDto
{
    public int Id { get; set; }
    public DateTime Created { get; set; }
    public int? TargetMetaverseAttributeId { get; set; }
    public string? TargetMetaverseAttributeName { get; set; }
    public int? TargetConnectedSystemAttributeId { get; set; }
    public string? TargetConnectedSystemAttributeName { get; set; }
    public string SourceType { get; set; } = null!;

    /// <summary>
    /// Inbound (import) text value-processing transforms applied to the value as it flows to the target
    /// Metaverse attribute. Only meaningful for import mappings targeting text attributes. Serialised as a
    /// comma-separated set of flag names.
    /// </summary>
    public InboundValueProcessing InboundValueProcessing { get; set; }

    /// <summary>
    /// Inbound (import) case normalisation applied to the text value. Only meaningful for import mappings
    /// targeting text attributes.
    /// </summary>
    public InboundCaseNormalisation CaseNormalisation { get; set; }

    /// <summary>
    /// Attribute priority for this import contribution (#91). Lower numbers win (1 is highest); int.MaxValue is
    /// the safe-addition sentinel meaning "not yet ordered, never wins". Only meaningful for import mappings.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// When true, a connected, in-scope contribution of null/absent for this attribute asserts "no value" and
    /// stops resolution falling through to lower-priority contributions ("Null is a value", #91). Only meaningful
    /// for import mappings.
    /// </summary>
    public bool NullIsValue { get; set; }

    /// <summary>
    /// When true, this mapping only flows during the initial provisioning (Create) export; afterwards the
    /// target attribute is unmanaged by JIM on that Connected System Object (#223). Only meaningful for
    /// export mappings.
    /// </summary>
    public bool InitialExportOnly { get; set; }

    /// <summary>
    /// Whether the mapping is evaluated at all (#1485). A disabled mapping is skipped by synchronisation in
    /// both directions until it is re-enabled. Applies to import and export mappings alike.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Why the mapping is disabled, when something disabled it on the administrator's behalf. Null when the
    /// mapping is enabled, and null when an administrator disabled it themselves.
    /// </summary>
    public string? DisabledReason { get; set; }

    public List<SyncRuleMappingSourceDto> Sources { get; set; } = new();

    /// <summary>
    /// The mapping's uniqueness token settings (Unique Value Generation, #242, Phase 3). Null unless
    /// <see cref="SourceType"/> is <c>GeneratedMapping</c>.
    /// </summary>
    public SyncRuleMappingGenerationDto? Generation { get; set; }

    public static SyncRuleMappingDto FromEntity(SyncRuleMapping entity)
    {
        return new SyncRuleMappingDto
        {
            Id = entity.Id,
            Created = entity.Created,
            TargetMetaverseAttributeId = entity.TargetMetaverseAttributeId,
            TargetMetaverseAttributeName = entity.TargetMetaverseAttribute?.Name,
            TargetConnectedSystemAttributeId = entity.TargetConnectedSystemAttributeId,
            TargetConnectedSystemAttributeName = entity.TargetConnectedSystemAttribute?.Name,
            SourceType = entity.GetSourceType().ToString(),
            InboundValueProcessing = entity.InboundValueProcessing,
            CaseNormalisation = entity.CaseNormalisation,
            Priority = entity.Priority,
            NullIsValue = entity.NullIsValue,
            InitialExportOnly = entity.InitialExportOnly,
            Enabled = entity.Enabled,
            DisabledReason = entity.DisabledReason,
            Sources = entity.Sources.Select(SyncRuleMappingSourceDto.FromEntity).ToList(),
            Generation = entity.Generation == null ? null : SyncRuleMappingGenerationDto.FromEntity(entity.Generation)
        };
    }
}

/// <summary>
/// API representation of a generated mapping's uniqueness token settings (Unique Value Generation, #242, Phase 3).
/// Exclusions and Collision Remediation are deliberately absent: exclusions are a release 4 surface and Collision
/// Remediation is a release 4 feature, so neither is exposed by any surface yet.
/// </summary>
public class SyncRuleMappingGenerationDto
{
    public GeneratedValueTokenKind TokenKind { get; set; }
    public GeneratedValueSuffixStyle SuffixStyle { get; set; }
    public int SuffixStart { get; set; }
    public long SequenceStart { get; set; }
    public int SequenceIncrement { get; set; }
    public int? FixedWidth { get; set; }
    public GeneratedValueWidthOverflowBehaviour OnWidthExceeded { get; set; }
    public GeneratedValueRandomFormat RandomFormat { get; set; }
    public int? RandomLength { get; set; }
    public string? Separator { get; set; }
    public int AttemptLimit { get; set; }
    public bool NeverReuse { get; set; }

    /// <summary>
    /// Present only on the response to a create or settings-update call that raised the target attribute's
    /// sequence counter because <see cref="SequenceStart"/> stood above its current position (plan decision 3).
    /// Null on every ordinary read.
    /// </summary>
    public SequenceSkippedAheadDto? SequenceSkippedAhead { get; set; }

    public static SyncRuleMappingGenerationDto FromEntity(SyncRuleMappingGeneration entity) => new()
    {
        TokenKind = entity.TokenKind,
        SuffixStyle = entity.SuffixStyle,
        SuffixStart = entity.SuffixStart,
        SequenceStart = entity.SequenceStart,
        SequenceIncrement = entity.SequenceIncrement,
        FixedWidth = entity.FixedWidth,
        OnWidthExceeded = entity.OnWidthExceeded,
        RandomFormat = entity.RandomFormat,
        RandomLength = entity.RandomLength,
        Separator = entity.Separator,
        AttemptLimit = entity.AttemptLimit,
        NeverReuse = entity.NeverReuse,
        SequenceSkippedAhead = entity.SequenceSkippedAhead == null
            ? null
            : new SequenceSkippedAheadDto { From = entity.SequenceSkippedAhead.From, To = entity.SequenceSkippedAhead.To }
    };
}

/// <summary>
/// Reports that a save moved a generated Sequence mapping's target attribute counter forward (plan decision 3).
/// </summary>
public class SequenceSkippedAheadDto
{
    public long From { get; set; }
    public long To { get; set; }
}

/// <summary>
/// Request DTO for a generated mapping's uniqueness token settings, on creation (Unique Value Generation, #242,
/// Phase 3). Server-side validation (<see cref="SyncRuleMappingGenerationValidator"/>) decides what combination
/// of these is actually valid for a given target attribute and token kind; this DTO only carries the values
/// through.
/// </summary>
public class CreateSyncRuleMappingGenerationRequest
{
    public GeneratedValueTokenKind TokenKind { get; set; }
    public GeneratedValueSuffixStyle SuffixStyle { get; set; } = GeneratedValueSuffixStyle.Number;
    public int SuffixStart { get; set; } = 1;
    public long SequenceStart { get; set; } = 1;
    public int SequenceIncrement { get; set; } = 1;
    public int? FixedWidth { get; set; }
    public GeneratedValueWidthOverflowBehaviour OnWidthExceeded { get; set; } = GeneratedValueWidthOverflowBehaviour.StopAndReport;
    public GeneratedValueRandomFormat RandomFormat { get; set; } = GeneratedValueRandomFormat.Guid;
    public int? RandomLength { get; set; }
    public string? Separator { get; set; }
    public int AttemptLimit { get; set; } = 1000;
    public bool NeverReuse { get; set; } = true;

    public SyncRuleMappingGeneration ToEntity() => new()
    {
        TokenKind = TokenKind,
        SuffixStyle = SuffixStyle,
        SuffixStart = SuffixStart,
        SequenceStart = SequenceStart,
        SequenceIncrement = SequenceIncrement,
        FixedWidth = FixedWidth,
        OnWidthExceeded = OnWidthExceeded,
        RandomFormat = RandomFormat,
        RandomLength = RandomLength,
        Separator = Separator,
        AttemptLimit = AttemptLimit,
        NeverReuse = NeverReuse
    };
}

/// <summary>
/// Request DTO for changing an existing generated mapping's uniqueness token settings (Unique Value Generation,
/// #242, Phase 3). Every field is optional and an omitted one leaves the mapping's current value alone, matching
/// <see cref="UpdateSyncRuleMappingRequest"/>'s own convention. Applying this to a mapping that is not currently
/// a generated mapping is refused; turning one into the other is not supported here (delete and create).
/// </summary>
public class UpdateSyncRuleMappingGenerationRequest
{
    public GeneratedValueTokenKind? TokenKind { get; set; }
    public GeneratedValueSuffixStyle? SuffixStyle { get; set; }
    public int? SuffixStart { get; set; }

    /// <summary>
    /// Raising this above the target attribute's counter moves the counter forward at save time (plan decision
    /// 3); the response's <see cref="SyncRuleMappingGenerationDto.SequenceSkippedAhead"/> reports the move. A
    /// lower or equal value has no effect.
    /// </summary>
    public long? SequenceStart { get; set; }

    public int? SequenceIncrement { get; set; }

    /// <summary><c>0</c> clears the fixed width; a positive value sets it; omitted (null) leaves it unchanged.</summary>
    public int? FixedWidth { get; set; }

    public GeneratedValueWidthOverflowBehaviour? OnWidthExceeded { get; set; }
    public GeneratedValueRandomFormat? RandomFormat { get; set; }
    public int? RandomLength { get; set; }

    /// <summary>An empty or whitespace-only string clears the stored separator; omitted (null) leaves it unchanged.</summary>
    public string? Separator { get; set; }

    public int? AttemptLimit { get; set; }
    public bool? NeverReuse { get; set; }

    public SyncRuleMappingGenerationSettingsUpdate ToSettingsUpdate() => new()
    {
        TokenKind = TokenKind,
        SuffixStyle = SuffixStyle,
        SuffixStart = SuffixStart,
        SequenceStart = SequenceStart,
        SequenceIncrement = SequenceIncrement,
        FixedWidth = FixedWidth,
        OnWidthExceeded = OnWidthExceeded,
        RandomFormat = RandomFormat,
        RandomLength = RandomLength,
        Separator = Separator,
        AttemptLimit = AttemptLimit,
        NeverReuse = NeverReuse
    };
}

/// <summary>
/// Request to change the settings on an existing Attribute Flow.
/// </summary>
/// <remarks>
/// Every field is optional and an omitted one leaves the mapping's current value alone; a request naming no
/// field at all is rejected rather than answered as a successful no-op. What the mapping targets, and whether
/// its source is an attribute or an Expression, cannot be changed here: those revalidate against attribute types
/// and reopen an import mapping's Attribute Priority position, so they remain a delete and a create.
/// </remarks>
public class UpdateSyncRuleMappingRequest
{
    /// <summary>
    /// Replaces the mapping's Expression. Expression mappings only; rejected for an attribute mapping, and for a
    /// mapping carrying more than one Expression source.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// What the Expression does when an attribute it reads has no value on the object being synchronised.
    /// Expression mappings only.
    /// </summary>
    public MissingInputBehaviour? MissingInputBehaviour { get; set; }

    /// <summary>
    /// Whether a contribution of no value from this mapping is authoritative ("Null is a value"). Import
    /// mappings only.
    /// </summary>
    public bool? NullIsValue { get; set; }

    /// <summary>
    /// Text value-processing transforms applied as the value flows to the Metaverse. Import mappings only.
    /// </summary>
    public InboundValueProcessing? InboundValueProcessing { get; set; }

    /// <summary>
    /// Case normalisation applied as the value flows to the Metaverse. Import mappings only.
    /// </summary>
    public InboundCaseNormalisation? CaseNormalisation { get; set; }

    /// <summary>
    /// Whether the mapping flows only during the initial provisioning export. Export mappings only.
    /// </summary>
    public bool? InitialExportOnly { get; set; }

    /// <summary>
    /// Enables or disables the mapping (#1485). A disabled mapping is skipped by synchronisation in both
    /// directions until it is re-enabled; re-enabling clears any recorded disabled reason. Applies to import
    /// and export mappings alike.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Changes to a generated mapping's uniqueness token settings (Unique Value Generation, #242, Phase 3).
    /// Refused for a mapping that is not currently a generated mapping.
    /// </summary>
    public UpdateSyncRuleMappingGenerationRequest? Generation { get; set; }

    /// <summary>
    /// Converts the request into the settings change the application layer understands.
    /// </summary>
    public SyncRuleMappingSettingsUpdate ToSettingsUpdate()
    {
        return new SyncRuleMappingSettingsUpdate
        {
            Expression = Expression,
            MissingInputBehaviour = MissingInputBehaviour,
            NullIsValue = NullIsValue,
            InboundValueProcessing = InboundValueProcessing,
            CaseNormalisation = CaseNormalisation,
            InitialExportOnly = InitialExportOnly,
            Enabled = Enabled,
            Generation = Generation?.ToSettingsUpdate()
        };
    }
}

/// <summary>
/// API representation of a SyncRuleMappingSource.
/// </summary>
public class SyncRuleMappingSourceDto
{
    public int Id { get; set; }
    public int Order { get; set; }
    public int? MetaverseAttributeId { get; set; }
    public string? MetaverseAttributeName { get; set; }
    public int? ConnectedSystemAttributeId { get; set; }
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// The expression to evaluate for this source.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// For expression sources: what happens when an attribute the expression reads has no value on the object
    /// being synchronised. EvaluateAnyway (the default) runs the expression regardless, ContributeNoValue skips it
    /// without reporting anything, FailMapping skips it and records an error, and FailObject leaves the whole
    /// object untouched.
    /// </summary>
    public MissingInputBehaviour MissingInputBehaviour { get; set; }

    public static SyncRuleMappingSourceDto FromEntity(SyncRuleMappingSource entity)
    {
        return new SyncRuleMappingSourceDto
        {
            Id = entity.Id,
            Order = entity.Order,
            MetaverseAttributeId = entity.MetaverseAttributeId,
            MetaverseAttributeName = entity.MetaverseAttribute?.Name,
            ConnectedSystemAttributeId = entity.ConnectedSystemAttributeId,
            ConnectedSystemAttributeName = entity.ConnectedSystemAttribute?.Name,
            Expression = entity.Expression,
            MissingInputBehaviour = entity.MissingInputBehaviour
        };
    }
}

/// <summary>
/// Request DTO for creating a new SyncRuleMapping.
/// </summary>
public class CreateSyncRuleMappingRequest
{
    /// <summary>
    /// For import rules: The target Metaverse Attribute ID.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// For export rules: The target Connected System Attribute ID.
    /// </summary>
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// For import rules only: inbound text value-processing transforms applied to the value as it flows to
    /// the target Metaverse attribute (a comma-separated set of flag names, e.g. "TreatWhitespaceAsNoValue,
    /// TrimWhitespace"). When omitted, defaults to TreatWhitespaceAsNoValue. Ignored for export rules.
    /// </summary>
    public InboundValueProcessing? InboundValueProcessing { get; set; }

    /// <summary>
    /// For import rules only: case normalisation applied to the inbound text value (None, Upper, Lower or
    /// Title). When omitted, defaults to None. Ignored for export rules.
    /// </summary>
    public InboundCaseNormalisation? CaseNormalisation { get; set; }

    /// <summary>
    /// For import rules only: when true, a connected, in-scope contribution of null/absent asserts "no value" and
    /// stops attribute priority resolution falling through to lower-priority contributions ("Null is a value", #91).
    /// When omitted, defaults to false. Ignored for export rules. Priority itself is not set at creation: a new
    /// import mapping lands at the safe-addition default (lowest priority) and is ordered later via the
    /// attribute-priority-order endpoint.
    /// </summary>
    public bool? NullIsValue { get; set; }

    /// <summary>
    /// For export rules only: when true, the mapping only flows during the initial provisioning (Create)
    /// export; afterwards the target attribute is unmanaged by JIM on that Connected System Object and
    /// Drift Correction does not re-assert it (#223). When omitted, defaults to false. Ignored for
    /// import rules.
    /// </summary>
    public bool? InitialExportOnly { get; set; }

    /// <summary>
    /// Whether the mapping is evaluated by synchronisation from the moment it is created (#1485). When omitted,
    /// defaults to true (enabled). Supply false to create the mapping disabled, so it can be ordered and
    /// reviewed before it starts flowing values; a disabled mapping is skipped in both directions until it is
    /// enabled. Applies to import and export rules alike.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// The sources for this mapping (attribute mappings or expressions).
    /// </summary>
    [Required]
    public List<CreateSyncRuleMappingSourceRequest> Sources { get; set; } = new();

    /// <summary>
    /// Makes this "JIM generates it" (Unique Value Generation, #242, Phase 3): the mapping's value is its base
    /// expression (read from <see cref="Sources"/>, optional for Sequence and Random tokens) plus a uniqueness
    /// token. Omit for an ordinary attribute or Expression mapping. Allowed on both import and export rules;
    /// direction gating follows the same rule as <see cref="InitialExportOnly"/>.
    /// </summary>
    public CreateSyncRuleMappingGenerationRequest? Generation { get; set; }
}

/// <summary>
/// Request DTO for creating a SyncRuleMappingSource.
/// </summary>
public class CreateSyncRuleMappingSourceRequest
{
    /// <summary>
    /// The order of this source in the mapping.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// For export rules: The source Metaverse Attribute ID.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// For import rules: The source Connected System Attribute ID.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// An expression to evaluate for this source.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// Example: "CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// For expression sources: what to do when an attribute the expression reads has no value on the object being
    /// synchronised. Omit for EvaluateAnyway, which runs the expression regardless and is what JIM has always
    /// done. ContributeNoValue skips the mapping and resolves by Attribute Priority without reporting anything;
    /// FailMapping skips it and records an ExpressionMissingInput error while the object's other attributes still
    /// flow; FailObject leaves the whole object untouched. Ignored for attribute sources.
    /// </summary>
    public MissingInputBehaviour? MissingInputBehaviour { get; set; }
}

/// <summary>
/// Request DTO for testing an expression with sample attribute data.
/// </summary>
public class TestExpressionRequest
{
    /// <summary>
    /// The expression to test.
    /// Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
    /// </summary>
    public string Expression { get; set; } = null!;

    /// <summary>
    /// Sample Metaverse attribute values to use during evaluation.
    /// Keys are attribute names, values are the attribute values.
    /// </summary>
    public Dictionary<string, object?>? MvAttributes { get; set; }

    /// <summary>
    /// Sample Connected System attribute values to use during evaluation.
    /// Keys are attribute names, values are the attribute values.
    /// </summary>
    public Dictionary<string, object?>? CsAttributes { get; set; }
}

/// <summary>
/// Response DTO for expression test results.
/// </summary>
public class TestExpressionResponse
{
    /// <summary>
    /// Indicates whether the expression is valid and evaluated successfully.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// The result of evaluating the expression (if successful).
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// The type of the result (e.g., "String", "Int32", "Boolean").
    /// </summary>
    public string? ResultType { get; set; }

    /// <summary>
    /// Error message if the expression is invalid or evaluation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Position in the expression where an error occurred (if applicable).
    /// </summary>
    public int? ErrorPosition { get; set; }
}
