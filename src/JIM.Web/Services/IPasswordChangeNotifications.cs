// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Services;

/// <summary>
/// The Password Synchronisation leg of the in-process notification relay (#1635). Raised as the database reports a
/// queue row inserted, updated or deleted, which is how a waiter learns a change has moved without polling for it.
/// <para>
/// Kept beside <see cref="IUiNotificationService"/> rather than added to it so the relay's existing consumers, and
/// the test doubles standing in for it, are untouched by a leg none of them listen to; the singleton implements
/// both. Like the other events this is a hint, not data: subscribers re-read through the application layer, and
/// keep a polling fallback for anything missed while the listener was disconnected.
/// </para>
/// </summary>
public interface IPasswordChangeNotifications
{
    /// <summary>
    /// Raised with the Connected System id whose queue changed. Bursts are coalesced, so subscribers receive at most
    /// one event per system per debounce window.
    /// </summary>
    event Action<int>? PasswordChangeChanged;
}
