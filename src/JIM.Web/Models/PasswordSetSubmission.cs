// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// What the Set Password dialog hands its host when the administrator submits (#1635): the accounts they ticked,
/// the password, and the two choices made beside it. The host turns this into a <c>SetPasswordRequest</c>, adding
/// the person and the initiator, which the dialog deliberately knows nothing about; it is a piece of user interface
/// with no knowledge of who is signed in or how JIM reaches a Connected System.
/// </summary>
/// <param name="Targets">The Connected System Object ids of the accounts to set the password on; never empty.</param>
/// <param name="Password">The password. Held by the dialog only while it is open, and by the host only for the call.</param>
/// <param name="ExpiryBehaviour">What should happen to the password once each system has it.</param>
/// <param name="EnableAccount">True to enable each account as its password is set; null to leave it as it is.</param>
public sealed record PasswordSetSubmission(
    IReadOnlyList<Guid> Targets,
    string Password,
    PasswordExpiryBehaviour ExpiryBehaviour,
    bool? EnableAccount);
