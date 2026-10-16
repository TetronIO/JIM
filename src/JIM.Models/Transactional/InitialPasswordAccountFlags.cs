// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// What a provisioning Synchronisation Rule's Initial Password promises about the account it is the first password
/// for (#2160): whether the account is enabled once the password is set, and whether its password never expires. Read
/// fresh at each delivery attempt from the rule's settings.
/// <para>
/// These are promises about the account, so they carry forward when another password is delivered as the account's
/// first one (a password change that was already waiting when the account was provisioned, or one that arrives before
/// the Initial Password lands). Passwords replace each other; promises about the account do not. "Must change at next
/// sign-in" is deliberately absent: it belongs to the password being delivered, never to the account.
/// </para>
/// </summary>
/// <param name="EnableAccount">The rule's "Enable the Connected System Object once the password is set".</param>
/// <param name="ExpiryBehaviour">
/// The rule's expiry, of which only one fact is a promise about the account: Never expires sets "Password never
/// expires", and the other two clear it.
/// </param>
public sealed record InitialPasswordAccountFlags(bool EnableAccount, PasswordExpiryBehaviour ExpiryBehaviour);
