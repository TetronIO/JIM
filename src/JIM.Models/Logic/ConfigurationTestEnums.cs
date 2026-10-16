// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What a configuration test case tests (#2055 Phase 4), and so which tester runs it.
/// </summary>
public enum ConfigurationTestCaseKind
{
    /// <summary>
    /// An Attribute Flow's Expression, through the evaluation pipeline synchronisation runs.
    /// </summary>
    AttributeFlow = 0,

    /// <summary>
    /// A Generated Value's base: its Attribute Flow run up to, and not including, adding the uniqueness token.
    /// </summary>
    GeneratedValue = 1,

    /// <summary>
    /// A Synchronisation Rule's scoping criteria: whether an object holding the inputs is in scope.
    /// </summary>
    Scope = 2,

    /// <summary>
    /// An Object Matching Rule's match values, without a Metaverse lookup.
    /// </summary>
    ObjectMatchingRule = 3
}

/// <summary>
/// Which configuration a test case's kind covers: the one reference it holds, and that is deleted with it.
/// </summary>
public enum ConfigurationTestCaseCoverage
{
    /// <summary>
    /// An Attribute Flow (<see cref="ConfigurationTestCase.SyncRuleMappingId"/>).
    /// </summary>
    AttributeFlow,

    /// <summary>
    /// A Synchronisation Rule (<see cref="ConfigurationTestCase.SyncRuleId"/>).
    /// </summary>
    SynchronisationRule,

    /// <summary>
    /// An Object Matching Rule (<see cref="ConfigurationTestCase.ObjectMatchingRuleId"/>).
    /// </summary>
    ObjectMatchingRule
}

/// <summary>
/// What a configuration test case expects its run to give.
/// </summary>
public enum ConfigurationTestExpectationKind
{
    /// <summary>
    /// Exactly these values (as an unordered set of values unless the case asks for their order). For an Object
    /// Matching Rule, its match values.
    /// </summary>
    Values = 0,

    /// <summary>
    /// No value: the Attribute Flow contributes none, the Generated Value's base waits, or the Object Matching Rule
    /// has no match value.
    /// </summary>
    NoValue = 1,

    /// <summary>
    /// An error, of any reason.
    /// </summary>
    Error = 2,

    /// <summary>
    /// Some value, whatever it is: for a result that differs from run to run, such as one that reads the clock.
    /// </summary>
    AnyValue = 3,

    /// <summary>
    /// The object is in the Synchronisation Rule's scope.
    /// </summary>
    InScope = 4,

    /// <summary>
    /// The object is not in the Synchronisation Rule's scope.
    /// </summary>
    OutOfScope = 5
}
