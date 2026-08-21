// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Connected System's Password Synchronisation configuration (#1119), as returned by the API.
/// <para>
/// A sub-resource of the Connected System rather than fields on <c>ConnectedSystemHeader</c>, for the same reason
/// the initial-password configuration is a sub-resource of its rule: the header is a flat list projection whose
/// query does not carry this navigation, so folding it in would report every system in a list as unconfigured.
/// </para>
/// <para>
/// <b>Carries no password.</b> Nothing in this configuration holds one; queued passwords live in the Password
/// Synchronisation queue, encrypted, and are never returned by any surface.
/// </para>
/// </summary>
public class ConnectedSystemPasswordSynchronisationResponse
{
    /// <summary>
    /// Whether Password Synchronisation has been configured on this Connected System at all. False means every
    /// other field below is JIM's default rather than somebody's choice.
    /// </summary>
    public bool Configured { get; set; }

    /// <summary>
    /// Whether this Connected System's Connector can set passwords. False means Password Synchronisation cannot
    /// be configured here, whatever else is sent.
    /// </summary>
    public bool ConnectorSupportsPasswordSet { get; set; }

    /// <summary>
    /// Whether queued password changes are delivered to this system. A configured but disabled system keeps
    /// accumulating them, and enabling it drains what accumulated.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The Connected System Object Type that receives passwords, i.e. the one holding this system's user accounts.
    /// </summary>
    public int TargetObjectTypeId { get; set; }

    /// <summary>
    /// The name of <see cref="TargetObjectTypeId"/>, so a caller does not need a second request to report it.
    /// </summary>
    public string? TargetObjectTypeName { get; set; }

    /// <summary>
    /// How many delivery attempts are made before a queued change is parked for an administrator. Zero means
    /// JIM's default applies; see <see cref="EffectiveMaxRetries"/> for what that resolves to.
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>
    /// The retry count actually applied, with JIM's default resolved.
    /// </summary>
    public int EffectiveMaxRetries { get; set; }

    /// <summary>
    /// The first retry interval; each subsequent attempt waits twice as long. Zero means JIM's default applies.
    /// </summary>
    public TimeSpan RetryBackoffBase { get; set; }

    /// <summary>
    /// The backoff base actually applied, with JIM's default resolved.
    /// </summary>
    public TimeSpan EffectiveRetryBackoffBase { get; set; }

    /// <summary>
    /// Whether JIM refuses to send a password to this system over a connection it cannot confirm is encrypted.
    /// <para>
    /// Reported here because it governs this feature, but it is set on the Connected System itself
    /// (<c>requireSecureTransport</c>) because it governs every password JIM sends to the system, including the
    /// initial password on an account it provisions and one an administrator sets by hand. Read-only on this
    /// resource; change it on the Connected System.
    /// </para>
    /// </summary>
    public bool RequireSecureTransport { get; set; }

    /// <summary>
    /// How long a queued password change waits for this system before it is expired rather than delivered.
    /// <para>
    /// Reported here because it governs this feature, but it is set on the Connected System itself
    /// (<c>initialPasswordTimeToLive</c>), which is shared with initial password provisioning: the question both
    /// ask is how long this system may be unavailable before JIM stops trying.
    /// </para>
    /// </summary>
    public TimeSpan EffectiveTimeToLive { get; set; }

    /// <summary>
    /// Builds a response for a Connected System, whether or not it has been configured.
    /// </summary>
    public static ConnectedSystemPasswordSynchronisationResponse FromEntity(
        ConnectedSystem connectedSystem,
        ConnectedSystemPasswordSynchronisation? configuration)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        // An unconfigured system reports JIM's defaults rather than zeroes, so a caller reading this to decide
        // what to send back does not have to know the defaults itself.
        var effective = configuration ?? new ConnectedSystemPasswordSynchronisation();

        return new ConnectedSystemPasswordSynchronisationResponse
        {
            Configured = configuration != null,
            ConnectorSupportsPasswordSet = connectedSystem.ConnectorDefinition?.SupportsPasswordSet ?? false,
            Enabled = effective.Enabled,
            TargetObjectTypeId = effective.TargetObjectTypeId,
            TargetObjectTypeName = configuration?.ResolveTargetObjectType(connectedSystem)?.Name,
            MaxRetries = effective.MaxRetries,
            EffectiveMaxRetries = effective.EffectiveMaxRetries,
            RetryBackoffBase = effective.RetryBackoffBase,
            EffectiveRetryBackoffBase = effective.EffectiveRetryBackoffBase,
            RequireSecureTransport = connectedSystem.RequireSecureTransport,
            EffectiveTimeToLive = connectedSystem.EffectiveInitialPasswordTimeToLive
        };
    }
}

/// <summary>
/// Changes to a Connected System's Password Synchronisation configuration. Every field is optional; an omitted
/// one leaves the stored value unchanged. Sending this against a system with no configuration creates one.
/// </summary>
public class UpdateConnectedSystemPasswordSynchronisationRequest
{
    /// <summary>
    /// Whether to deliver queued password changes to this system. Turning this on drains whatever accumulated
    /// while it was off; turning it off keeps accumulating rather than discarding.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// The Connected System Object Type that holds this system's user accounts. Required when creating a
    /// configuration; it must be an Object Type of this system that is selected for synchronisation.
    /// </summary>
    public int? TargetObjectTypeId { get; set; }

    /// <summary>
    /// How many delivery attempts to make before parking a queued change. Zero uses JIM's default.
    /// </summary>
    public int? MaxRetries { get; set; }

    /// <summary>
    /// The first retry interval; each subsequent attempt waits twice as long, capped at the change's time to
    /// live. Zero uses JIM's default.
    /// </summary>
    public TimeSpan? RetryBackoffBase { get; set; }

    /// <summary>
    /// An optional reason for the change, recorded against the Connected System's configuration change history.
    /// </summary>
    public string? ChangeReason { get; set; }
}
