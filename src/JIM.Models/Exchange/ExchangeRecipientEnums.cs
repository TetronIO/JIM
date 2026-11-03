// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exchange;

/// <summary>
/// What a declared recipient type does to the recipient an import last read back (#2021, PRD requirements 15 and 16).
/// </summary>
public enum ExchangeRecipientChangeKind
{
    /// <summary>The object is not a recipient yet and is declared one: Exchange enables it.</summary>
    Enable = 0,

    /// <summary>The object is already the recipient type it is declared: Exchange updates its alias and addresses.</summary>
    Update = 1,

    /// <summary>
    /// The object is a recipient of one type and is declared another, or declared not to be one. Not made in the first
    /// release: it waits for leaver handling.
    /// </summary>
    TypeChange = 2
}

/// <summary>
/// What export staging does with an object's declared Exchange recipient state (#2021).
/// </summary>
public enum ExchangeRecipientDecisionKind
{
    /// <summary>Declared not to be a recipient, and not one: there is nothing to do and nothing is written.</summary>
    NotARecipient = 0,

    /// <summary>The declared type does not fit the object (requirement 5): reported, never attempted.</summary>
    Refused = 1,

    /// <summary>The declared type would change an existing recipient's type (requirement 16): reported, never attempted.</summary>
    TypeChangeNotSupported = 2,

    /// <summary>A change Exchange can make: queued for the Exchange delivery lane.</summary>
    Stage = 3
}
