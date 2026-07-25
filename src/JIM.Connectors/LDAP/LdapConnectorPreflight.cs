// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Establishes whether the password channel to a directory is likely to work, without setting a password on
/// anything.
/// <para>
/// Every check here is a read. That is a deliberate ceiling rather than a limitation of effort: proving a password
/// set works means really setting one, and every route to doing that against a directory JIM did not create is a
/// password reset against somebody's account. The checks instead cover what surrounds the password, which is where
/// most failures actually are.
/// </para>
/// <para>
/// The connection itself is established by the caller, because a failure to connect is the first finding rather
/// than something this class can report on.
/// </para>
/// </summary>
internal class LdapConnectorPreflight
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;
    private readonly LdapDirectoryType _directoryType;
    private readonly bool _supportsPasswordModifyExtension;
    private readonly bool _isConnectionEncrypted;

    internal LdapConnectorPreflight(
        ILdapOperationExecutor executor,
        ILogger logger,
        LdapDirectoryType directoryType,
        bool supportsPasswordModifyExtension,
        bool isConnectionEncrypted)
    {
        _executor = executor;
        _logger = logger;
        _directoryType = directoryType;
        _supportsPasswordModifyExtension = supportsPasswordModifyExtension;
        _isConnectionEncrypted = isConnectionEncrypted;
    }

    private bool IsActiveDirectory =>
        _directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// Runs every check that can be answered once a connection is open.
    /// </summary>
    /// <param name="containerExternalIds">
    /// The Distinguished Names of the containers this Connected System manages. Rights are checked in these,
    /// because a directory grants them per part of the tree and checking elsewhere answers a question nobody asked.
    /// </param>
    /// <param name="domainRootDn">The domain naming context, where the target publishes one.</param>
    internal async Task<List<PasswordPreflightCheckResult>> RunAsync(
        IReadOnlyList<string> containerExternalIds,
        string? domainRootDn,
        CancellationToken cancellationToken)
    {
        var checks = new List<PasswordPreflightCheckResult>
        {
            CheckEncryption(),
            CheckPasswordMechanism()
        };

        cancellationToken.ThrowIfCancellationRequested();
        checks.Add(CheckResetRights(containerExternalIds));

        cancellationToken.ThrowIfCancellationRequested();
        checks.Add(await CheckPolicyDiscoveryAsync(domainRootDn));

        return checks;
    }

    /// <summary>
    /// Whether the channel carrying the password is encrypted.
    /// <para>
    /// A warning rather than a failure, because JIM allows an unencrypted password channel: some directories
    /// genuinely cannot serve TLS, and locking those deployments out of password management entirely helps nobody.
    /// The administrator gets to make that call, having been told plainly what it costs.
    /// </para>
    /// </summary>
    private PasswordPreflightCheckResult CheckEncryption()
    {
        if (_isConnectionEncrypted)
            return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Encryption,
                "The connection is encrypted, so passwords are protected in transit.");

        var details = new List<string>
        {
            "Enable the 'Use Secure Connection' setting on this Connected System, and set the port to the one the directory serves LDAPS on."
        };

        // Active Directory refuses the write itself rather than accepting it insecurely, so for those targets this
        // is very likely to be the thing that stops a password set outright. It is still not reported as a failure:
        // a signed and sealed bind satisfies Active Directory without TLS, and JIM cannot tell from here whether
        // one is in use.
        if (IsActiveDirectory)
            details.Add("Active Directory refuses to accept a password over a connection that is neither encrypted nor signed and sealed, so a password set is likely to be rejected until this is addressed.");

        return PasswordPreflightCheckResult.Warning(PasswordPreflightCheck.Encryption,
            "The connection is not encrypted. Passwords would be sent where anyone on the network path can read them.",
            details);
    }

    /// <summary>
    /// Whether the mechanism JIM would use to set a password is available on this target.
    /// </summary>
    private PasswordPreflightCheckResult CheckPasswordMechanism()
    {
        if (IsActiveDirectory)
            return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PasswordMechanism,
                $"JIM would set passwords by writing the '{LdapConnectorPassword.AttributeUnicodePwd}' attribute, which is how Active Directory accepts them.");

        if (_supportsPasswordModifyExtension)
            return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PasswordMechanism,
                "JIM would set passwords using the LDAP Password Modify extended operation, which this directory advertises support for.");

        // Writing userPassword directly is the obvious alternative and JIM will not do it: a directory applies its
        // configured hashing to the extended operation, but stores a directly written attribute exactly as it is
        // given. That would put cleartext passwords into the directory, so an unsupported target is refused.
        return PasswordPreflightCheckResult.Failed(PasswordPreflightCheck.PasswordMechanism,
            "This directory does not advertise the LDAP Password Modify extended operation, which is the only mechanism JIM will use to set a password on a directory that is not Active Directory.",
            [
                $"JIM looks for the extended operation '{LdapConnectorPassword.PasswordModifyExtensionOid}' on the directory's rootDSE.",
                "JIM will not write a password attribute directly instead, because a directory stores a directly written value exactly as given rather than hashing it, which would leave cleartext passwords in the directory."
            ]);
    }

    /// <summary>
    /// Whether the account JIM connects as can reset a password on the objects it would provision.
    /// <para>
    /// <b>JIM cannot currently establish this, and says so rather than guessing.</b> The obvious mechanism does not
    /// work, and its failure mode is bad enough to be worth recording here so that nobody reaches for it again.
    /// Active Directory publishes a constructed attribute, allowedAttributesEffective, listing the attributes the
    /// calling account may write on an object. Reading it and looking for unicodePwd looks like exactly the right
    /// question, and is wrong: [MS-ADTS] 3.1.1.4.5.7 computes that list purely from a RIGHT_DS_WRITE_PROPERTY
    /// check, whereas [MS-ADTS] 3.1.1.3.1.5.1 grants a password reset through the "User-Force-Change-Password"
    /// control access right instead. The two are disjoint, so an account delegated password resets in the normal,
    /// least-privileged way has no write permission on unicodePwd and would be reported as having no rights, while
    /// a Domain Admin, holding everything, would be reported as having them.
    /// </para>
    /// <para>
    /// That is worse than not checking: it passes for the over-privileged account someone tests with and fails for
    /// the correctly configured one they deploy, which is precisely backwards. Answering the question properly
    /// means reading the target's nTSecurityDescriptor and evaluating its access control list client-side against
    /// the reset-password right, which is real work and not yet done. Until it is, this reports an unknown and
    /// says where the rights need to hold so an administrator can check them directly.
    /// </para>
    /// </summary>
    private PasswordPreflightCheckResult CheckResetRights(IReadOnlyList<string> containerExternalIds)
    {
        var details = new List<string>();

        if (IsActiveDirectory)
            details.Add("The account needs the 'Reset Password' permission on the objects JIM provisions. It does not need to be a Domain Admin, and should not be.");
        else
            details.Add("This directory publishes no way for a client to ask what it is allowed to do, so the account's rights have to be confirmed at the directory itself.");

        // Naming where rights actually need to hold is the useful half of this check, and the half JIM can answer.
        // Permissions are granted per part of a directory, so "the service account can reset passwords" is not a
        // question with one answer.
        if (containerExternalIds.Count > 0)
        {
            details.Add(containerExternalIds.Count == 1
                ? "JIM would be provisioning into this container, so that is where the permission needs to be granted:"
                : $"JIM would be provisioning into these {containerExternalIds.Count} containers, so that is where the permission needs to be granted:");
            details.AddRange(containerExternalIds.Select(dn => $"    {dn}"));
        }
        else
        {
            details.Add("No containers are selected on this Connected System yet, so JIM cannot say where the permission will need to be granted.");
        }

        return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.ResetRights,
            "JIM cannot confirm whether the account it connects as is allowed to reset passwords. This is the most common reason a password set fails, so it is worth checking at the Connected System directly.",
            details);
    }

    /// <summary>
    /// Whether the target's password policy could be read, so that a generator can be pre-filled from it.
    /// <para>
    /// Never a failure. An unreadable policy does not stop a password being set; it means the administrator
    /// configures the generator from what they know rather than from what the target published, and finds out
    /// about a mismatch through a rejection instead of up front.
    /// </para>
    /// </summary>
    private async Task<PasswordPreflightCheckResult> CheckPolicyDiscoveryAsync(string? domainRootDn)
    {
        if (!IsActiveDirectory)
            return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PolicyDiscovery,
                "This directory does not publish a password policy that a client can read, so JIM cannot pre-fill the password generator from it.",
                ["Set the password requirements on the Synchronisation Rule to match the target's policy by hand, and expect a rejection to be how a mismatch shows up."]);

        var policyReader = new LdapConnectorPasswordPolicy(_executor, _logger, _directoryType);
        var policy = await policyReader.GetPasswordPolicyAsync(domainRootDn ?? string.Empty);

        if (policy == null || !policy.HasAnyDiscoveredConstraint)
            return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PolicyDiscovery,
                "JIM could not read the domain password policy, so it cannot pre-fill the password generator from it.",
                ["Check that the account JIM connects as can read the domain root object."]);

        var details = new List<string>();
        if (policy.MinimumLength is { } minimumLength)
            details.Add($"Minimum length: {minimumLength}.");
        if (policy.ComplexityRequired is { } complexityRequired)
            details.Add(complexityRequired
                ? $"Complexity is required: a password must use at least {policy.RequiredCharacterClassCount} of the 5 character categories."
                : "Complexity is not required.");

        // Worth surfacing here as well as on the policy panel: a preflight is what an administrator runs when they
        // want to know whether this will work, and "the policy JIM read may not be the policy that applies" is
        // exactly the caveat that belongs in that answer.
        if (policy.FineGrainedPolicySignal != FineGrainedPolicySignal.Absent)
            details.Add(policy.FineGrainedPolicySignal == FineGrainedPolicySignal.Present
                ? "This domain has Fine-Grained Password Policies, which apply stricter rules to some accounts. What JIM read is a floor, not the whole story."
                : "JIM could not establish whether this domain has Fine-Grained Password Policies, which would apply stricter rules to some accounts. Treat what it read as a floor.");

        return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PolicyDiscovery,
            "JIM read the domain password policy and can pre-fill the password generator from it.",
            details);
    }
}
