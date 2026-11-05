// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Models.Exchange;
using JIM.Models.Transactional;
using Tetron.PowerShellRemoting;
using Tetron.PowerShellRemoting.Kerberos;

namespace JIM.Connectors.LDAP;

/// <summary>
/// The LDAP Connector's side of the Exchange delivery lane (#2021, requirements 8, 9, 15 to 17 and 26): one session for
/// the lane, and each change converged over it. A change reads the recipient, runs only what converges it, and reads
/// again after an enable, so a recipient already in its declared state costs one read and nothing else.
/// </summary>
/// <remarks>
/// A failed change is a classified result, never an exception, so one recipient never stops the lane: only cancellation
/// propagates. A session that cannot be opened throws <see cref="ExchangeSessionException"/>, which the lane records
/// against every change it claimed.
/// </remarks>
internal sealed class LdapExchangeRecipientDelivery
{
    /// <summary>
    /// The most read-then-act rounds one change takes: enable, then set what the enable could not, with one spare for an
    /// enable that someone else's raced.
    /// </summary>
    private const int MaximumRounds = 3;

    private readonly Func<RemoteSessionOptions, CancellationToken, Task<ILdapExchangeRecipientSession>> _openSession;
    private readonly Func<KerberosRealm, CancellationToken, Task> _ensureRealm;
    private ILdapExchangeRecipientSession? _session;

    /// <param name="openSession">Opens an Exchange session with the options given.</param>
    /// <param name="ensureRealm">Makes sure this process's Kerberos configuration knows the realm and its KDC.</param>
    internal LdapExchangeRecipientDelivery(Func<RemoteSessionOptions, CancellationToken, Task<ILdapExchangeRecipientSession>> openSession,
        Func<KerberosRealm, CancellationToken, Task> ensureRealm)
    {
        _openSession = openSession;
        _ensureRealm = ensureRealm;
    }

    /// <summary>Delivery as JIM runs it: real Exchange sessions, and this process's own Kerberos configuration.</summary>
    internal static LdapExchangeRecipientDelivery ForThisProcess() => new(
        LdapExchangeRemoteRecipientSession.OpenAsync,
        (realm, cancellationToken) => ExchangeKerberosConfiguration.Process.EnsureRealmAsync(realm, cancellationToken));

    /// <summary>What the open session has cost so far, or nothing when none is open.</summary>
    internal ExchangeSessionStatistics Statistics => _session?.Statistics ?? ExchangeSessionStatistics.None;

    /// <summary>
    /// Signs in to Exchange as the directory account. Throws <see cref="ExchangeSessionException"/>, with the failure's
    /// kind and the settings check's words for it, when the sign-in fails.
    /// </summary>
    internal async Task OpenAsync(LdapExchangeKerberosSignIn signIn, Uri endpoint, string password, X509Certificate2Collection trustedCertificates,
        CancellationToken cancellationToken)
    {
        Close();
        try
        {
            await _ensureRealm(new KerberosRealm(signIn.Realm, [signIn.Kdc]), cancellationToken);
            _session = await _openSession(new RemoteSessionOptions
            {
                ConnectionUri = endpoint,
                ConfigurationName = LdapExchangeSettingsCheck.ExchangeConfigurationName,
                UserPrincipalName = signIn.Principal,
                Password = password,
                TrustedCertificates = trustedCertificates
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the lane being stopped is not a failure; anything else is the session's, in the settings check's words.
            var kind = LdapExchangeFailureClassifier.Classify("Sign-in", RemoteFailure.FromException(ex)).Kind;
            throw new ExchangeSessionException(kind, LdapExchangeCheckFailures.Describe(ex, signIn, endpoint), ex);
        }
    }

    /// <summary>
    /// Converges <paramref name="target"/> on <paramref name="declared"/>. A refusal or failure is a result; only
    /// cancellation throws.
    /// </summary>
    internal async Task<ExchangeConvergeResult> ConvergeAsync(ExchangeRecipientTarget target, ExchangeDeclaredState declared, CancellationToken cancellationToken)
    {
        if (_session == null)
            return ExchangeConvergeResult.Failure(ExchangeChangeFailureKind.Unreachable, "No Exchange session is open.");

        var changedAnything = false;
        LdapExchangeCommandException? enableFailure = null;
        try
        {
            for (var round = 0; round < MaximumRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await _session.ReadAsync(target, cancellationToken);

                // An enable that failed with still no recipient there failed for its own reason: report that, not a retry.
                if (enableFailure != null && current.RecipientType == ExchangeRecipientTypes.None)
                    return Failed(enableFailure, declared);

                var plan = LdapExchangeConvergePlanner.Plan(target, current, declared);
                if (plan.Refusal != null)
                    return ExchangeConvergeResult.Failure(ExchangeChangeFailureKind.Refused, plan.Refusal);
                if (plan.Commands.Count == 0)
                    return ExchangeConvergeResult.Success(changedAnything);

                enableFailure = await RunAsync(plan, cancellationToken);
                if (enableFailure != null)
                    continue;

                changedAnything = true;
                if (!plan.ReadAgainAfter)
                    return ExchangeConvergeResult.Success(changedAnything);
            }

            return enableFailure != null ? Failed(enableFailure, declared) : ExchangeConvergeResult.Success(changedAnything);
        }
        catch (LdapExchangeCommandException ex)
        {
            return Failed(ex, declared);
        }
    }

    /// <summary>
    /// Runs a plan's commands. An enable Exchange rejects is handed back rather than thrown, because someone may have
    /// enabled the recipient since JIM read it: the next read tells.
    /// </summary>
    private async Task<LdapExchangeCommandException?> RunAsync(LdapExchangeConvergePlan plan, CancellationToken cancellationToken)
    {
        foreach (var command in plan.Commands)
        {
            try
            {
                await _session!.RunAsync(command, cancellationToken);
            }
            catch (LdapExchangeCommandException ex) when (plan.ReadAgainAfter && ex.Failure.Kind == RemoteFailureKind.Command && !IsHidden(ex))
            {
                return ex;
            }
        }

        return null;
    }

    private static bool IsHidden(LdapExchangeCommandException ex) =>
        LdapExchangeFailureClassifier.Classify(ex.Command, ex.Failure).Kind == ExchangeChangeFailureKind.InsufficientPermissions;

    private static ExchangeConvergeResult Failed(LdapExchangeCommandException ex, ExchangeDeclaredState declared)
    {
        var (kind, message) = LdapExchangeFailureClassifier.Classify(ex.Command, ex.Failure, declared.Database);
        return ExchangeConvergeResult.Failure(kind, message);
    }

    /// <summary>Closes the session. Safe when none is open.</summary>
    internal void Close()
    {
        _session?.Dispose();
        _session = null;
    }
}
