// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Models.Activities;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;

namespace JIM.Models.Logic;

/// <summary>
/// A saved configuration test case (#2055 Phase 4): inputs, and what the configuration it covers should give for
/// them, so a change that breaks the expectation is caught. It covers exactly one thing, an Attribute Flow, a
/// Synchronisation Rule's Scope or an Object Matching Rule, and is deleted with it.
/// <para>
/// Inputs are configuration, stored unencrypted as other configuration is. They can hold personal data when they were
/// loaded from a real object, which is why the portal says so and lets each value be replaced.
/// </para>
/// </summary>
public class ConfigurationTestCase : IAuditable
{
    /// <summary>
    /// The longest name a test case may have.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// The longest description a test case may have.
    /// </summary>
    public const int MaxDescriptionLength = 2_000;

    /// <summary>
    /// The most inputs a test case may give. Every case is read and hashed into each snapshot of what owns it, so its
    /// size is bounded where it enters, on every surface.
    /// </summary>
    public const int MaxInputs = 100;

    /// <summary>
    /// The most values one input may hold, and the most values a case may expect.
    /// </summary>
    public const int MaxValues = 100;

    /// <summary>
    /// The longest an input's or an expected value may be, in characters.
    /// </summary>
    public const int MaxValueLength = 4_000;

    /// <summary>
    /// The longest an input's attribute name may be.
    /// </summary>
    public const int MaxAttributeNameLength = 400;

    /// <summary>
    /// The most test cases one owner may hold: a Synchronisation Rule, or a Connected System's Simple mode Object
    /// Matching Rules. Every save of what a case covers runs it twice in the save gate, so the number is bounded.
    /// </summary>
    public const int MaxTestCasesPerOwner = 200;

    // The stored-JSON conventions (MvoDeletionPolicySnapshot, ConfigurationSnapshotService): camelCase, enums as text.
    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    public int Id { get; set; }

    /// <summary>
    /// What the case checks, in the administrator's words.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ConfigurationTestCaseKind Kind { get; set; }

    /// <summary>
    /// The Attribute Flow an <see cref="ConfigurationTestCaseKind.AttributeFlow"/> or
    /// <see cref="ConfigurationTestCaseKind.GeneratedValue"/> case covers; null for any other kind.
    /// </summary>
    public int? SyncRuleMappingId { get; set; }
    public SyncRuleMapping? SyncRuleMapping { get; set; }

    /// <summary>
    /// The Synchronisation Rule a <see cref="ConfigurationTestCaseKind.Scope"/> case covers; null for any other kind.
    /// </summary>
    public int? SyncRuleId { get; set; }
    public SyncRule? SyncRule { get; set; }

    /// <summary>
    /// The Object Matching Rule an <see cref="ConfigurationTestCaseKind.ObjectMatchingRule"/> case covers; null for
    /// any other kind.
    /// </summary>
    public int? ObjectMatchingRuleId { get; set; }
    public ObjectMatchingRule? ObjectMatchingRule { get; set; }

    /// <summary>
    /// The instant to run the case as of: what <c>Now()</c> and <c>Today()</c> read, and what relative date scoping
    /// criteria resolve against. Null runs it as of the time it runs.
    /// </summary>
    public DateTime? RunAsOfUtc { get; set; }

    /// <summary>
    /// The inputs as stored: a JSON array of <see cref="ConfigurationTestInput"/>. Read and write
    /// <see cref="Inputs"/> instead.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string InputsJson { get; set; } = "[]";

    /// <summary>
    /// The expectation as stored: a JSON <see cref="ConfigurationTestExpectation"/>. Read and write
    /// <see cref="Expectation"/> instead.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string ExpectationJson { get; set; } = "{}";

    /// <summary>
    /// The inputs, one per attribute; an attribute not given has no value.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<ConfigurationTestInput> Inputs
    {
        get => (string.IsNullOrWhiteSpace(InputsJson)
                   ? null
                   : JsonSerializer.Deserialize<List<ConfigurationTestInput>>(InputsJson, SerialiserOptions))
               ?? [];
        set => InputsJson = JsonSerializer.Serialize(value ?? [], SerialiserOptions);
    }

    /// <summary>
    /// What the case expects. A case with nothing stored expects any value.
    /// </summary>
    [NotMapped]
    public ConfigurationTestExpectation Expectation
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExpectationJson) || ExpectationJson.Trim() == "{}")
                return new ConfigurationTestExpectation(ConfigurationTestExpectationKind.AnyValue);
            var read = JsonSerializer.Deserialize<ConfigurationTestExpectation>(ExpectationJson, SerialiserOptions)
                       ?? new ConfigurationTestExpectation(ConfigurationTestExpectationKind.AnyValue);
            // A document written with no values reads back with them null, whatever the constructor defaulted.
            return read.Values is null ? read with { Values = [] } : read;
        }
        set => ExpectationJson = JsonSerializer.Serialize(value, SerialiserOptions);
    }

    public DateTime Created { get; set; } = DateTime.UtcNow;
    public ActivityInitiatorType CreatedByType { get; set; }
    public Guid? CreatedById { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? LastUpdated { get; set; }
    public ActivityInitiatorType LastUpdatedByType { get; set; }
    public Guid? LastUpdatedById { get; set; }
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The one reference a case of <paramref name="kind"/> holds.
    /// </summary>
    public static ConfigurationTestCaseCoverage CoveredReference(ConfigurationTestCaseKind kind) => kind switch
    {
        ConfigurationTestCaseKind.AttributeFlow or ConfigurationTestCaseKind.GeneratedValue => ConfigurationTestCaseCoverage.AttributeFlow,
        ConfigurationTestCaseKind.Scope => ConfigurationTestCaseCoverage.SynchronisationRule,
        ConfigurationTestCaseKind.ObjectMatchingRule => ConfigurationTestCaseCoverage.ObjectMatchingRule,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a configuration test case kind.")
    };

    /// <summary>
    /// The expectations a case of <paramref name="kind"/> can hold.
    /// </summary>
    public static IReadOnlyList<ConfigurationTestExpectationKind> ExpectationsFor(ConfigurationTestCaseKind kind) =>
        kind == ConfigurationTestCaseKind.Scope
            ? [ConfigurationTestExpectationKind.InScope, ConfigurationTestExpectationKind.OutOfScope]
            : [ConfigurationTestExpectationKind.Values, ConfigurationTestExpectationKind.NoValue,
               ConfigurationTestExpectationKind.Error, ConfigurationTestExpectationKind.AnyValue];

    /// <summary>
    /// Why the case cannot be saved as it is, or null when it can: its name, the one thing it covers, an expectation
    /// its kind can give, and inputs that each name one attribute once.
    /// </summary>
    public string? DescribeInvalidity()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "A test case needs a name.";
        if (Name.Length > MaxNameLength)
            return $"A test case's name can be at most {MaxNameLength} characters.";
        if (Description?.Length > MaxDescriptionLength)
            return $"A test case's description can be at most {MaxDescriptionLength:N0} characters.";

        var references = (SyncRuleMappingId.HasValue ? 1 : 0) + (SyncRuleId.HasValue ? 1 : 0) + (ObjectMatchingRuleId.HasValue ? 1 : 0);
        if (references > 1)
            return "A test case covers exactly one thing: an Attribute Flow, a Synchronisation Rule or an Object Matching Rule.";

        var covered = CoveredReference(Kind);
        var coversIt = covered switch
        {
            ConfigurationTestCaseCoverage.AttributeFlow => SyncRuleMappingId.HasValue,
            ConfigurationTestCaseCoverage.SynchronisationRule => SyncRuleId.HasValue,
            _ => ObjectMatchingRuleId.HasValue
        };
        if (!coversIt)
            return covered switch
            {
                ConfigurationTestCaseCoverage.AttributeFlow => $"A {Kind} test case covers an Attribute Flow; name the Attribute Flow it tests.",
                ConfigurationTestCaseCoverage.SynchronisationRule => "A Scope test case covers a Synchronisation Rule; name the Synchronisation Rule whose Scope it tests.",
                _ => "An Object Matching Rule test case covers an Object Matching Rule; name the Object Matching Rule it tests."
            };

        var expectation = Expectation;
        if (!ExpectationsFor(Kind).Contains(expectation.Kind))
            return $"A {Kind} test case cannot expect {expectation.Kind}; it can expect " +
                   $"{string.Join(", ", ExpectationsFor(Kind))}.";
        if (expectation.Kind == ConfigurationTestExpectationKind.Values && expectation.Values.Count == 0)
            return "A test case expecting Values needs at least one value; to expect none, expect NoValue.";
        if (expectation.Kind != ConfigurationTestExpectationKind.Values && expectation.Values.Count > 0)
            return $"Expected values are given only when expecting Values, not {expectation.Kind}.";
        if (expectation.Kind != ConfigurationTestExpectationKind.Values && expectation.InOrder)
            return $"An expected order is given only when expecting Values, not {expectation.Kind}.";
        if (expectation.Values.Count > MaxValues)
            return $"A test case can expect at most {MaxValues} values; this one expects {expectation.Values.Count}.";
        if (expectation.Values.Any(value => value.Length > MaxValueLength))
            return $"Each expected value can be at most {MaxValueLength:N0} characters.";

        var inputs = Inputs;
        if (inputs.Count > MaxInputs)
            return $"A test case can give at most {MaxInputs} inputs; this one gives {inputs.Count}.";

        var readsBothSides = covered == ConfigurationTestCaseCoverage.AttributeFlow;
        var seen = new HashSet<(ExpressionInputSource?, string)>();
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.AttributeName))
                return "Each test input needs an attribute name.";
            if (input.AttributeName.Length > MaxAttributeNameLength)
                return $"An input's attribute name can be at most {MaxAttributeNameLength} characters.";
            if (input.Values.Count > MaxValues)
                return $"The input '{input.AttributeName}' has {input.Values.Count} values; an input can hold at most {MaxValues}.";
            if (input.Values.Any(value => value.Length > MaxValueLength))
                return $"A value of the input '{input.AttributeName}' is longer than {MaxValueLength:N0} characters, the most a value can be.";
            if (readsBothSides && input.Source == null)
                return $"The input '{input.AttributeName}' needs a side to read it from: cs for the Connected System Object, mv for the Metaverse Object.";
            if (!readsBothSides && input.Source != null)
                return $"The input '{input.AttributeName}' names a side, but a {Kind} test case reads only one, so give the attribute name alone.";
            if (!seen.Add((input.Source, input.AttributeName.ToUpperInvariant())))
                return $"The input '{input.AttributeName}' is given more than once; give all its values in one input.";
        }

        return null;
    }
}
