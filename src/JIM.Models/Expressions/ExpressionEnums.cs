// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// Which side of the Metaverse an Expression reads an input from.
/// </summary>
public enum ExpressionInputSource
{
    /// <summary>
    /// A Metaverse Object attribute, written as mv["Attribute Name"].
    /// </summary>
    Metaverse,

    /// <summary>
    /// A Connected System Object attribute, written as cs["Attribute Name"].
    /// </summary>
    ConnectedSystem
}

/// <summary>
/// What an Expression-based Attribute Flow does when an attribute it reads has no value on the object being
/// synchronised.
/// </summary>
/// <remarks>
/// The same absent input is a data-corruption incident for one Expression and entirely routine for the next. An
/// Expression built on <c>IIF</c> or <c>IsNullOrEmpty</c> handles the absence itself and must be left alone; a
/// concatenation building a Distinguished Name produces a syntactically valid string that no layer downstream can
/// tell from a good one. This is therefore a per-mapping choice rather than a rule JIM can make (#1361).
/// </remarks>
public enum MissingInputBehaviour
{
    /// <summary>
    /// Evaluate the Expression with the input absent and contribute whatever it returns. The behaviour JIM has
    /// always had, and the default, so no existing mapping changes on upgrade.
    /// </summary>
    EvaluateAnyway,

    /// <summary>
    /// Do not evaluate, and treat that as a legitimate outcome rather than a fault: the mapping contributes
    /// nothing, resolved by Attribute Priority and "Null is a value" like any other no-value outcome. Nothing is
    /// reported as an error.
    /// </summary>
    ContributeNoValue,

    /// <summary>
    /// Do not evaluate, and record it as an error against the object. Every other Attribute Flow on the
    /// Synchronisation Rule still runs, and the target attribute keeps whatever it already held.
    /// </summary>
    FailMapping,

    /// <summary>
    /// Do not evaluate anything for this object: no Attribute Flow on this Synchronisation Rule runs, nothing
    /// flows and nothing is exported. For an input feeding identity-critical output, where a partially populated
    /// object is worse than none.
    /// </summary>
    FailObject
}

/// <summary>
/// What an Expression's result gives the attribute it flows to, once converted to that attribute's type (#2179).
/// </summary>
public enum ExpressionResultResolutionKind
{
    /// <summary>
    /// No value: the result was null, an empty collection, or values that all collapsed to nothing.
    /// </summary>
    NoValue,

    /// <summary>
    /// One or more distinct values, each converted to the attribute's type.
    /// </summary>
    Values,

    /// <summary>
    /// A value the attribute's type cannot hold, such as text that is not a number for a Number attribute. Nothing
    /// is stored and the existing value is kept; the failure is reported.
    /// </summary>
    NotConvertible,

    /// <summary>
    /// More than one distinct value for a single-valued attribute. JIM will not pick one, so nothing is stored and
    /// the existing value is kept; the failure is reported.
    /// </summary>
    SeveralValues
}

/// <summary>
/// How far an Attribute Flow's Expression got, before its result is resolved against the target attribute (#2055):
/// it returned a result, it supplied no value, or it failed.
/// </summary>
public enum ExpressionSourceOutcome
{
    /// <summary>
    /// The Expression was evaluated and returned a result, which is not yet converted to the target's type.
    /// </summary>
    Result,

    /// <summary>
    /// No value: Missing Input Behaviour is Contribute no value and an input is absent, or the Expression returned
    /// null.
    /// </summary>
    NoValue,

    /// <summary>
    /// The Expression was not evaluated because an input is absent and Missing Input Behaviour is Fail this mapping.
    /// </summary>
    MissingInputFailedMapping,

    /// <summary>
    /// The Expression was not evaluated because an input is absent and Missing Input Behaviour is Fail the object.
    /// </summary>
    MissingInputFailedObject,

    /// <summary>
    /// The Expression failed while it was evaluated.
    /// </summary>
    EvaluationFailed
}

/// <summary>
/// What an Attribute Flow's Expression gives its target attribute once the whole pipeline has run (#2055): values,
/// no value, or an error. Synchronisation and the tester read the same outcome.
/// </summary>
public enum AttributeFlowOutcome
{
    /// <summary>
    /// One or more values, each converted to the target attribute's type.
    /// </summary>
    Values,

    /// <summary>
    /// No value, resolved by Attribute Priority and "Null is a value" on import; nothing is staged on export.
    /// </summary>
    NoValue,

    /// <summary>
    /// An error: the attribute keeps its current value, and the error is reported.
    /// </summary>
    Error
}

/// <summary>
/// Why an Attribute Flow produced an error (#2055).
/// </summary>
public enum AttributeFlowErrorReason
{
    /// <summary>
    /// An input has no value and Missing Input Behaviour is Fail this mapping: the mapping is reported and skipped.
    /// </summary>
    MissingInputFailedMapping,

    /// <summary>
    /// An input has no value and Missing Input Behaviour is Fail the object: nothing on the object is synchronised.
    /// </summary>
    MissingInputFailedObject,

    /// <summary>
    /// The Expression failed while it was evaluated: nothing on the object is synchronised.
    /// </summary>
    EvaluationFailed,

    /// <summary>
    /// The Expression returned a value the target attribute's type cannot hold.
    /// </summary>
    ResultNotConvertible,

    /// <summary>
    /// The Expression returned several values for a single-valued target attribute.
    /// </summary>
    ResultMultiValued,

    /// <summary>
    /// A Generated Value's base expression returned more than one value. A base must be one text value, since the
    /// uniqueness token is added to exactly one candidate.
    /// </summary>
    GeneratedBaseNotSingleValue
}

/// <summary>
/// What a Generated Value's base expression gives generation (#2055, #242): the base the uniqueness token is added
/// to, a wait, or an error. Generation itself, which needs the database, is not part of it.
/// </summary>
public enum GeneratedBaseOutcome
{
    /// <summary>
    /// A base value, after the mapping's value processing. Generation adds the uniqueness token to it.
    /// </summary>
    Base,

    /// <summary>
    /// No base yet: an input is absent and Missing Input Behaviour is Contribute no value, the Expression returned no
    /// value, or value processing left nothing. Synchronisation waits; a value already generated is kept, never
    /// cleared.
    /// </summary>
    Waits,

    /// <summary>
    /// The mapping has no base expression, which a Sequence or Random token allows: the token alone is the value.
    /// </summary>
    NoBaseExpression,

    /// <summary>
    /// An error: nothing is generated this time, and the error is reported.
    /// </summary>
    Error
}

/// <summary>
/// One transform of an import Attribute Flow's value processing (#843), as the tester's trace names it (#2055). Listed
/// in the order synchronisation applies them.
/// </summary>
public enum AttributeFlowValueProcessingStep
{
    /// <summary>
    /// Leading and trailing whitespace is removed.
    /// </summary>
    TrimWhitespace,

    /// <summary>
    /// Runs of internal whitespace become a single space.
    /// </summary>
    CollapseInternalWhitespace,

    /// <summary>
    /// The value is changed to upper case.
    /// </summary>
    UpperCase,

    /// <summary>
    /// The value is changed to lower case.
    /// </summary>
    LowerCase,

    /// <summary>
    /// The value is changed to title case.
    /// </summary>
    TitleCase,

    /// <summary>
    /// A value that is empty or only whitespace becomes no value.
    /// </summary>
    TreatWhitespaceAsNoValue
}

/// <summary>
/// The group an expression function belongs to in the function reference and the expression editor's function list
/// (#2053). Each is one section of the reference, in this order.
/// </summary>
public enum ExpressionFunctionCategory
{
    /// <summary>Text functions: transforming and testing text.</summary>
    Text,

    /// <summary>Regular expression functions, with a time limit.</summary>
    Pattern,

    /// <summary>Choosing between values, and comparing them.</summary>
    Conditional,

    /// <summary>Converting a value to another type.</summary>
    Conversion,

    /// <summary>Dates and times.</summary>
    Date,

    /// <summary>Distinguished name helpers.</summary>
    DistinguishedName,

    /// <summary>Generating passwords and passphrases.</summary>
    Password,

    /// <summary>Working on every value of a Multi-Valued attribute at once.</summary>
    MultiValued,

    /// <summary>Active Directory userAccountControl flags.</summary>
    AccountControl
}

/// <summary>
/// The type of value an expression function returns (#2055 Phase 3), named as the attribute type it suits. A function
/// given a Multi-Valued attribute returns one such value per value it is given, so this is the type of each value, not
/// how many there are. Saving refuses an expression whose outermost call returns a type its target can never hold.
/// </summary>
public enum ExpressionResultType
{
    /// <summary>Not known until it runs: the function returns one of its arguments (<c>Coalesce</c>, <c>IIF</c>).</summary>
    Unknown,

    /// <summary>Text.</summary>
    Text,

    /// <summary>A whole number that fits a Number attribute.</summary>
    Number,

    /// <summary>A whole number that may need a Long Number attribute.</summary>
    LongNumber,

    /// <summary>A decimal number.</summary>
    Decimal,

    /// <summary>A date and time.</summary>
    DateTime,

    /// <summary>True or false.</summary>
    Boolean
}
