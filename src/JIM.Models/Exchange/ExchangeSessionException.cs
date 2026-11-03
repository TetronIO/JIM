// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Exchange;

/// <summary>
/// An Exchange session could not be opened (#2021): Exchange is unreachable, or the sign-in failed. Carries how, so
/// the delivery lane can record it against the changes it was about to converge. The message never carries a
/// credential.
/// </summary>
public sealed class ExchangeSessionException : Exception
{
    public ExchangeSessionException(ExchangeChangeFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public ExchangeSessionException(ExchangeChangeFailureKind failureKind, string message, Exception innerException)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    /// <summary>How the session failed to open.</summary>
    public ExchangeChangeFailureKind FailureKind { get; }
}
