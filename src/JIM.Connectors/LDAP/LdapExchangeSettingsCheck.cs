// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using Tetron.PowerShellRemoting;
using Tetron.PowerShellRemoting.Kerberos;

namespace JIM.Connectors.LDAP;

/// <summary>
/// The LDAP Connector's Exchange settings check (#2021, PRD requirements 2 and 3), run when the settings are saved with
/// Exchange recipients managed: it signs in to Exchange as the directory account, refuses the save when the sign-in
/// fails, warns when the account cannot run some of the commands JIM needs, and learns the Exchange version and the
/// sign-in it used. The session is always closed.
/// </summary>
internal sealed class LdapExchangeSettingsCheck
{
    /// <summary>The endpoint configuration Exchange's remote PowerShell is published as.</summary>
    internal const string ExchangeConfigurationName = "Microsoft.Exchange";

    internal const string VersionFactName = "Exchange";
    internal const string SignInFactName = "Exchange Sign-in";

    private readonly Func<RemoteSessionOptions, CancellationToken, Task<ILdapExchangeCheckSession>> _openSession;
    private readonly Func<KerberosRealm, CancellationToken, Task> _ensureRealm;

    /// <param name="openSession">Opens an Exchange session with the options given.</param>
    /// <param name="ensureRealm">Makes sure this process's Kerberos configuration knows the realm and its KDC.</param>
    internal LdapExchangeSettingsCheck(Func<RemoteSessionOptions, CancellationToken, Task<ILdapExchangeCheckSession>> openSession,
        Func<KerberosRealm, CancellationToken, Task> ensureRealm)
    {
        _openSession = openSession;
        _ensureRealm = ensureRealm;
    }

    /// <summary>
    /// The check as JIM runs it: real Exchange sessions, and this process's own Kerberos configuration.
    /// </summary>
    internal static LdapExchangeSettingsCheck ForThisProcess() => new(
        LdapExchangeRemoteCheckSession.OpenAsync,
        (realm, cancellationToken) => ExchangeKerberosConfiguration.Process.EnsureRealmAsync(realm, cancellationToken));

    /// <summary>
    /// Signs in to Exchange and reports what the settings will and will not do.
    /// </summary>
    /// <param name="signIn">The Kerberos sign-in worked out from the directory settings.</param>
    /// <param name="endpoint">Exchange's remote PowerShell endpoint, from the Exchange server setting.</param>
    /// <param name="password">The directory account's password. Never logged.</param>
    /// <param name="trustedCertificates">The certificates JIM trusts, for an https:// endpoint.</param>
    /// <param name="serverSetting">The Exchange server setting, which refusals and warnings are about.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="cancellationToken">Stops the check; a cancelled check throws rather than reporting a failure.</param>
    internal async Task<ConnectorSettingsCheckResult> CheckAsync(LdapExchangeKerberosSignIn signIn, Uri endpoint, string password,
        X509Certificate2Collection trustedCertificates, ConnectedSystemSettingValue? serverSetting, ILogger logger, CancellationToken cancellationToken)
    {
        await _ensureRealm(new KerberosRealm(signIn.Realm, [signIn.Kdc]), cancellationToken);

        var options = new RemoteSessionOptions
        {
            ConnectionUri = endpoint,
            ConfigurationName = ExchangeConfigurationName,
            UserPrincipalName = signIn.Principal,
            Password = password,
            TrustedCertificates = trustedCertificates
        };

        ILdapExchangeCheckSession session;
        try
        {
            session = await _openSession(options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Any failure to sign in refuses the save, in words the administrator can act on; only the caller stopping
            // the check is not a failure.
            logger.Warning("Exchange settings check: signing in to {Endpoint} as {Principal} failed: {Message}",
                LogSanitiser.Sanitise(endpoint.ToString()), LogSanitiser.Sanitise(signIn.Principal), LogSanitiser.Sanitise(ex.Message));
            return Refused(serverSetting, LdapExchangeCheckFailures.Describe(ex, signIn, endpoint));
        }

        using (session)
        {
            var result = new ConnectorSettingsCheckResult();
            try
            {
                var visible = await session.GetVisibleCommandsAsync(LdapExchangeCommandCoverage.RequiredCommands, cancellationToken);
                if (LdapExchangeCommandCoverage.WarningFor(signIn.Principal, visible) is { } warning)
                    result.Results.Add(ConnectorSettingValueValidationResult.Warning(serverSetting, warning));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Signed in, so the settings work; what could not be told is which recipients the account may manage.
                logger.Warning("Exchange settings check: listing the commands {Principal} can run failed: {Message}",
                    LogSanitiser.Sanitise(signIn.Principal), LogSanitiser.Sanitise(ex.Message));
                result.Results.Add(ConnectorSettingValueValidationResult.Warning(serverSetting,
                    $"JIM signed in to Exchange as {signIn.Principal}, but could not tell which commands the account may run, because Get-Command failed: {ex.Message}"));
            }

            if (await session.GetServerVersionAsync(endpoint.Host, cancellationToken) is { } version)
                result.Facts.Add(new ConnectorCapability { Name = VersionFactName, Value = version });
            result.Facts.Add(new ConnectorCapability { Name = SignInFactName, Value = $"{signIn.Principal}, by Kerberos through {signIn.Kdc}" });

            return result;
        }
    }

    private static ConnectorSettingsCheckResult Refused(ConnectedSystemSettingValue? serverSetting, string message) => new()
    {
        Results = [new ConnectorSettingValueValidationResult { SettingValue = serverSetting, IsValid = false, ErrorMessage = message }]
    };
}
