// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web.Extensions.Api;

namespace JIM.Web.Models.Api;

/// <summary>
/// What a saved configuration test case expects.
/// </summary>
public class ConfigurationTestExpectationDto
{
    /// <summary>
    /// The outcome expected. An Attribute Flow, Generated Value or Object Matching Rule case expects <c>Values</c>,
    /// <c>NoValue</c>, <c>Error</c> or <c>AnyValue</c>; a Scope case expects <c>InScope</c> or <c>OutOfScope</c>.
    /// </summary>
    [Required]
    public ConfigurationTestExpectationKind Kind { get; set; }

    /// <summary>
    /// For <c>Values</c>, the values expected: text, a number or a Boolean, a date in ISO 8601. Each is compared with
    /// what the configuration gives as a value of its type, whatever the server's culture. Empty otherwise.
    /// </summary>
    public List<object?>? Values { get; set; }

    /// <summary>
    /// For <c>Values</c>, whether they must come in this order. By default they compare in any order, each repeat
    /// counted.
    /// </summary>
    public bool InOrder { get; set; }

    public static ConfigurationTestExpectationDto FromModel(ConfigurationTestExpectation expectation) => new()
    {
        Kind = expectation.Kind,
        Values = expectation.Values.Cast<object?>().ToList(),
        InOrder = expectation.InOrder
    };

    public ConfigurationTestExpectation ToModel() =>
        new(Kind, TestExpressionSampleValues.ToInvariantText(Values), InOrder);
}

/// <summary>
/// A saved configuration test case: inputs, and what the configuration it covers should give for them.
/// </summary>
public class ConfigurationTestCaseDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// What the case tests: <c>AttributeFlow</c>, <c>GeneratedValue</c>, <c>Scope</c> or <c>ObjectMatchingRule</c>.
    /// </summary>
    public ConfigurationTestCaseKind Kind { get; set; }

    /// <summary>
    /// The Attribute Flow an <c>AttributeFlow</c> or <c>GeneratedValue</c> case covers.
    /// </summary>
    public int? SyncRuleMappingId { get; set; }

    /// <summary>
    /// The Synchronisation Rule a <c>Scope</c> case covers.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The Object Matching Rule an <c>ObjectMatchingRule</c> case covers.
    /// </summary>
    public int? ObjectMatchingRuleId { get; set; }

    /// <summary>
    /// The instant the case runs as of, in UTC: what <c>Now()</c> and <c>Today()</c> read, and what relative date
    /// criteria resolve against. Null runs it as of the time it runs.
    /// </summary>
    public DateTime? RunAsOf { get; set; }

    /// <summary>
    /// For an Attribute Flow or Generated Value case, the Connected System Object's values by attribute name: a list
    /// of text values, empty for an attribute with no value. Left out of a list unless it asks for
    /// <c>include=inputs</c>.
    /// </summary>
    public Dictionary<string, List<string>>? CsAttributes { get; set; }

    /// <summary>
    /// For an Attribute Flow or Generated Value case, the Metaverse Object's values by attribute name. Left out of a
    /// list unless it asks for <c>include=inputs</c>.
    /// </summary>
    public Dictionary<string, List<string>>? MvAttributes { get; set; }

    /// <summary>
    /// For a Scope or Object Matching Rule case, the object's values by attribute name. Left out of a list unless it
    /// asks for <c>include=inputs</c>.
    /// </summary>
    public Dictionary<string, List<string>>? Attributes { get; set; }

    public ConfigurationTestExpectationDto Expectation { get; set; } = new();

    public DateTime Created { get; set; }

    public string? CreatedByName { get; set; }

    public DateTime? LastUpdated { get; set; }

    public string? LastUpdatedByName { get; set; }

    public static ConfigurationTestCaseDto FromModel(ConfigurationTestCase testCase, bool includeInputs)
    {
        var dto = new ConfigurationTestCaseDto
        {
            Id = testCase.Id,
            Name = testCase.Name,
            Description = testCase.Description,
            Kind = testCase.Kind,
            SyncRuleMappingId = testCase.SyncRuleMappingId,
            SyncRuleId = testCase.SyncRuleId,
            ObjectMatchingRuleId = testCase.ObjectMatchingRuleId,
            RunAsOf = testCase.RunAsOfUtc,
            Expectation = ConfigurationTestExpectationDto.FromModel(testCase.Expectation),
            Created = testCase.Created,
            CreatedByName = testCase.CreatedByName,
            LastUpdated = testCase.LastUpdated,
            LastUpdatedByName = testCase.LastUpdatedByName
        };

        if (!includeInputs)
            return dto;

        var inputs = testCase.Inputs;
        if (ConfigurationTestCase.CoveredReference(testCase.Kind) == ConfigurationTestCaseCoverage.AttributeFlow)
        {
            dto.CsAttributes = Side(inputs, ExpressionInputSource.ConnectedSystem);
            dto.MvAttributes = Side(inputs, ExpressionInputSource.Metaverse);
        }
        else
        {
            dto.Attributes = Side(inputs, null);
        }

        return dto;
    }

    private static Dictionary<string, List<string>> Side(IReadOnlyList<ConfigurationTestInput> inputs, ExpressionInputSource? source) =>
        inputs.Where(input => input.Source == source).ToDictionary(input => input.AttributeName, input => input.Values.ToList());
}

/// <summary>
/// A configuration test case to save. Its inputs take the shape the configuration testers take, so a test tried
/// through <c>test-expression</c>, <c>scoping-criteria/test</c> or <c>matching-rules/{ruleId}/test</c> can be saved
/// with the same values.
/// </summary>
public class CreateConfigurationTestCaseRequest : ConfigurationTestCaseContentRequest
{
    /// <summary>
    /// What the case tests: <c>AttributeFlow</c>, <c>GeneratedValue</c>, <c>Scope</c> or <c>ObjectMatchingRule</c>.
    /// </summary>
    [Required]
    public ConfigurationTestCaseKind? Kind { get; set; }

    /// <summary>
    /// For an <c>AttributeFlow</c> or <c>GeneratedValue</c> case, the Attribute Flow it covers.
    /// </summary>
    public int? SyncRuleMappingId { get; set; }

    /// <summary>
    /// For a <c>Scope</c> case, the Synchronisation Rule whose Scope it covers.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// For an <c>ObjectMatchingRule</c> case, the Object Matching Rule it covers.
    /// </summary>
    public int? ObjectMatchingRuleId { get; set; }
}

/// <summary>
/// Changes to a saved configuration test case. Anything left out stays as it is. What the case covers and what it
/// tests cannot change; create another case to test something else.
/// </summary>
public class UpdateConfigurationTestCaseRequest
{
    /// <summary>
    /// A new name. Left out (or null), the name stays as it is.
    /// </summary>
    [StringLength(ConfigurationTestCase.MaxNameLength, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// A new description. Left out (or null), it stays as it is; an empty string clears it.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// For an Attribute Flow or Generated Value case, the Connected System Object's values, replacing those the case
    /// holds for that side. Left out (or null), they stay as they are.
    /// </summary>
    public Dictionary<string, object?>? CsAttributes { get; set; }

    /// <summary>
    /// For an Attribute Flow or Generated Value case, the Metaverse Object's values, replacing those the case holds
    /// for that side. Left out (or null), they stay as they are.
    /// </summary>
    public Dictionary<string, object?>? MvAttributes { get; set; }

    /// <summary>
    /// For a Scope or Object Matching Rule case, the object's values, replacing those the case holds. Left out (or
    /// null), they stay as they are.
    /// </summary>
    public Dictionary<string, object?>? Attributes { get; set; }

    /// <summary>
    /// A new instant to run the case as of, in UTC. Left out (or null), it stays as it is; to run as of the time the
    /// case runs instead, set <c>clearRunAsOf</c>.
    /// </summary>
    public DateTime? RunAsOf { get; set; }

    /// <summary>
    /// True to run the case as of the time it runs rather than a fixed instant. Cannot be combined with
    /// <c>runAsOf</c>.
    /// </summary>
    public bool ClearRunAsOf { get; set; }

    /// <summary>
    /// A new expectation, replacing the one the case holds. Left out (or null), it stays as it is.
    /// </summary>
    public ConfigurationTestExpectationDto? Expectation { get; set; }
}

/// <summary>
/// What a configuration test case holds beyond what it covers.
/// </summary>
public abstract class ConfigurationTestCaseContentRequest
{
    /// <summary>
    /// What the case checks, in your own words.
    /// </summary>
    [Required]
    [StringLength(ConfigurationTestCase.MaxNameLength, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// For an Attribute Flow or Generated Value case, the Connected System Object's values by attribute name: text, a
    /// number, a Boolean, null for no value, or an array of these for several values. Give every attribute the
    /// configuration reads, with null for one that should have no value.
    /// </summary>
    public Dictionary<string, object?>? CsAttributes { get; set; }

    /// <summary>
    /// For an Attribute Flow or Generated Value case, the Metaverse Object's values by attribute name, as for
    /// <c>csAttributes</c>.
    /// </summary>
    public Dictionary<string, object?>? MvAttributes { get; set; }

    /// <summary>
    /// For a Scope or Object Matching Rule case, the object's values by attribute name, as for <c>csAttributes</c>.
    /// </summary>
    public Dictionary<string, object?>? Attributes { get; set; }

    /// <summary>
    /// The instant to run the case as of, in UTC. Omit it to run as of the time the case runs; set it for a
    /// configuration that reads the clock, so the case gives the same result every time.
    /// </summary>
    public DateTime? RunAsOf { get; set; }

    [Required]
    public ConfigurationTestExpectationDto? Expectation { get; set; }

    /// <summary>
    /// The inputs as a test case holds them, every value as culture-invariant text.
    /// </summary>
    public List<ConfigurationTestInput> ToInputs() =>
        TestExpressionSampleValues.ToTestCaseInputs(CsAttributes, ExpressionInputSource.ConnectedSystem)
            .Concat(TestExpressionSampleValues.ToTestCaseInputs(MvAttributes, ExpressionInputSource.Metaverse))
            .Concat(TestExpressionSampleValues.ToTestCaseInputs(Attributes, null))
            .ToList();
}

/// <summary>
/// Which saved configuration test cases to run: give exactly one of a Synchronisation Rule, a Connected System or
/// test case ids.
/// </summary>
public class RunConfigurationTestsRequest
{
    /// <summary>
    /// Run every case covering this Synchronisation Rule: its Scope, its Attribute Flows and its own Object Matching
    /// Rules.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Run every case under this Connected System: each of its Synchronisation Rules', and its Simple mode Object
    /// Matching Rules'.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// Run these cases.
    /// </summary>
    public List<int>? Ids { get; set; }
}

/// <summary>
/// One configuration test case's run, computed now and never stored.
/// </summary>
public class ConfigurationTestRunResultDto
{
    public int TestCaseId { get; set; }

    public string TestCaseName { get; set; } = string.Empty;

    public ConfigurationTestCaseKind Kind { get; set; }

    /// <summary>
    /// <c>Passed</c>; <c>Failed</c> (<c>expected</c> and <c>actual</c> say how); <c>NeedsReview</c>, when the case
    /// no longer fits the configuration and was not run (<c>problems</c> says how); or <c>Error</c>, when it could not
    /// be run.
    /// </summary>
    public ConfigurationTestStatus Status { get; set; }

    /// <summary>
    /// One sentence on how the run came out and why.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    public List<string> Problems { get; set; } = [];

    public ConfigurationTestExpectationDto Expected { get; set; } = new();

    /// <summary>
    /// What the configuration gave; null when the case was not run.
    /// </summary>
    public ConfigurationTestActualDto? Actual { get; set; }

    /// <summary>
    /// The instant, in UTC, the case ran as of.
    /// </summary>
    public DateTime RanAsOf { get; set; }

    /// <summary>
    /// True when the case has no run-as-of time and what it covers reads the clock, so its result can change from one
    /// run to the next.
    /// </summary>
    public bool RanAsOfNow { get; set; }

    public static ConfigurationTestRunResultDto FromModel(ConfigurationTestRunResult result) => new()
    {
        TestCaseId = result.TestCaseId,
        TestCaseName = result.TestCaseName,
        Kind = result.Kind,
        Status = result.Status,
        Summary = result.Summary,
        Problems = result.Problems.ToList(),
        Expected = ConfigurationTestExpectationDto.FromModel(result.Expected),
        Actual = result.Actual == null ? null : new ConfigurationTestActualDto
        {
            Outcome = result.Actual.Outcome,
            Values = result.Actual.Values.ToList(),
            ErrorMessage = result.Actual.ErrorMessage
        },
        RanAsOf = result.RanAsOfUtc,
        RanAsOfNow = result.RanAsOfNow
    };
}

/// <summary>
/// What the configuration gave when a test case ran.
/// </summary>
public class ConfigurationTestActualDto
{
    public ConfigurationTestOutcome Outcome { get; set; }

    /// <summary>
    /// For <c>Values</c>, the values, as culture-invariant text: a date in ISO 8601 UTC, a number with a full stop.
    /// </summary>
    public List<string> Values { get; set; } = [];

    public string? ErrorMessage { get; set; }
}
