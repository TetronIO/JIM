// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Resources;
using JIM.Models.Localisation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Connectors.Http;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// The Connector's password channel: what JIM's Initial Password, Set Password and Password Synchronisation deliver
/// through to reach Entra ID. A password is written with <c>PATCH /users/{id}</c> and its <c>passwordProfile</c>.
/// </summary>
/// <remarks>
/// <para>
/// Entra ID keeps "must change at next sign-in" (<c>forceChangePasswordNextSignIn</c>) and "never expires"
/// (<c>DisablePasswordExpiration</c> in <c>passwordPolicies</c>) apart and honours both together, so every expiry
/// behaviour JIM offers is applied as asked; Active Directory's conflict between the two does not arise.
/// </para>
/// <para>
/// Graph's app-only password reset needs <c>User-PasswordProfile.ReadWrite.All</c> and, beyond any permission, the app
/// holding at least the User Administrator directory role. Even so, an administrator's password is out of reach, and a
/// user synchronised from Active Directory or federated to another identity provider keeps its password there.
/// </para>
/// </remarks>
internal sealed class GraphConnectorPasswords
{
    private const string NeverExpiresPolicy = "DisablePasswordExpiration";

    private readonly GraphHttpClient _client;
    private readonly ILogger _logger;

    public GraphConnectorPasswords(GraphHttpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Entra ID's rules for a cloud user's password, which every tenant applies alike.</summary>
    /// <remarks>
    /// Eight to 256 characters, three of four categories, and not the password the account has now. Each domain's expiry
    /// period needs Domain.Read.All, which the Connector does not ask for, so it is left unread rather than guessed.
    /// Entra ID also refuses passwords on its banned list (and any custom list the tenant keeps), which nothing can read.
    /// </remarks>
    public static ConnectedSystemPasswordPolicy Policy() => new()
    {
        MinimumLength = 8,
        ComplexityRequired = true,
        RequiredCharacterClassCount = 3,
        RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase | PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol,
        PasswordHistoryLength = 1,
        // Entra ID has no per-object policy that demands more than this; DisableStrongPassword only ever demands less.
        PolicyOverrideSignal = PolicyOverrideSignal.Absent,
        FurtherChecksApply = true,
        DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read
    };

    public async Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken)
    {
        if (!string.Equals(target.Type?.Name, GraphSchemaCatalogue.UserObjectType, StringComparison.OrdinalIgnoreCase))
            return PasswordSetResult.Failed(PasswordSetFailureReason.UnsupportedOperation, "Only a user has a password in Microsoft Entra ID.");

        var id = target.ExternalIdAttributeValue?.StringValue;
        if (string.IsNullOrWhiteSpace(id))
            return PasswordSetResult.Failed(PasswordSetFailureReason.TargetObjectNotFound, "The Connected System Object has no External ID, so the Entra ID user cannot be found.");

        var path = $"users/{Uri.EscapeDataString(id)}";
        try
        {
            var held = await ReadPasswordPoliciesAsync(path, cancellationToken);
            var neverExpiresHeld = held.Contains(NeverExpiresPolicy, StringComparer.OrdinalIgnoreCase);
            var neverExpires = options.LeavePasswordNeverExpiresAsIs
                ? neverExpiresHeld
                : options.ExpiryBehaviour == PasswordExpiryBehaviour.NeverExpires;

            var body = new JsonObject
            {
                ["passwordProfile"] = new JsonObject
                {
                    ["password"] = password,
                    ["forceChangePasswordNextSignIn"] = options.ExpiryBehaviour == PasswordExpiryBehaviour.RequireChangeAtNextSignIn
                }
            };

            if (neverExpires != neverExpiresHeld)
            {
                var policies = held.Where(p => !p.Equals(NeverExpiresPolicy, StringComparison.OrdinalIgnoreCase)).ToList();
                if (neverExpires)
                    policies.Add(NeverExpiresPolicy);
                body["passwordPolicies"] = policies.Count == 0 ? "None" : string.Join(", ", policies);
            }

            if (options.EnableAccount is { } enable)
                body["accountEnabled"] = enable;

            // The password is a write that cannot be taken back, so the token is not handed to it once it is on its way.
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await _client.SendAsync(HttpMethod.Patch, path, body, null, CancellationToken.None);

            var applied = options.ExpiryBehaviour == PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy && neverExpires
                ? PasswordExpiryBehaviour.NeverExpires
                : options.ExpiryBehaviour == PasswordExpiryBehaviour.NeverExpires && !neverExpires
                    ? PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy
                    : options.ExpiryBehaviour;
            var result = PasswordSetResult.Succeeded(applied);
            result.PasswordNeverExpiresChanged = neverExpires != neverExpiresHeld;
            return result;
        }
        catch (GraphRequestException ex)
        {
            _logger.Warning("Microsoft Graph refused a password for user {Id}: HTTP {Status} {Code}.", LogSanitiser.Sanitise(id), (int)ex.StatusCode, LogSanitiser.Sanitise(ex.ErrorCode));
            return Classify(ex);
        }
        catch (HttpAuthenticationException ex)
        {
            return PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault, $"No access token could be acquired from Microsoft Entra ID: {ex.Message}");
        }
        catch (ServerCertificateRejectedException ex)
        {
            return PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault, ex.Message);
        }
    }

    /// <summary>
    /// The user's <c>passwordPolicies</c>, so "never expires" is set or cleared without disturbing anything else it
    /// carries, such as DisableStrongPassword.
    /// </summary>
    private async Task<List<string>> ReadPasswordPoliciesAsync(string path, CancellationToken cancellationToken)
    {
        var user = await _client.SendJsonAsync<JsonElement>(HttpMethod.Get, $"{path}?$select=id,passwordPolicies", null, null, cancellationToken);
        var value = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("passwordPolicies", out var policies) && policies.ValueKind == JsonValueKind.String
            ? policies.GetString()
            : null;
        return (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Equals("None", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static PasswordSetResult Classify(GraphRequestException ex)
    {
        var said = $"Microsoft Graph refused the password (HTTP {(int)ex.StatusCode}{(ex.ErrorCode == null ? "" : $", {ex.ErrorCode}")}): {ex.Message}";
        var message = ex.Message;

        if (ex.StatusCode == HttpStatusCode.NotFound)
            return PasswordSetResult.Failed(PasswordSetFailureReason.TargetObjectNotFound, said);

        if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault,
                $"{said} Setting a password needs the {GraphPermissions.UserPasswordProfileReadWrite} application permission and the app holding the User Administrator " +
                "directory role over this user (Privileged Authentication Administrator for an administrator's password).");
        }

        if (message.Contains("on-premises", StringComparison.OrdinalIgnoreCase) || message.Contains("federated", StringComparison.OrdinalIgnoreCase))
        {
            return PasswordSetResult.Failed(PasswordSetFailureReason.UnsupportedOperation,
                $"{said} The user is synchronised from on-premises Active Directory or signs in through another identity provider, which keeps its password; " +
                "set it there, through the LDAP Connector for Active Directory.");
        }

        if (message.Contains("password", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("complexity", StringComparison.OrdinalIgnoreCase) || message.Contains("policy", StringComparison.OrdinalIgnoreCase)
                || message.Contains("banned", StringComparison.OrdinalIgnoreCase) || message.Contains("history", StringComparison.OrdinalIgnoreCase)))
            return PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, said);

        return (int)ex.StatusCode is 408 or 429 or >= 500
            ? PasswordSetResult.Failed(PasswordSetFailureReason.Transient, said)
            : PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault, said);
    }

    /// <summary>
    /// Checks the channel without setting a password: a token can be had, the channel is encrypted (always: Graph is
    /// HTTPS), the token grants a permission that writes password profiles, and the app holds a role that resets them.
    /// </summary>
    public static async Task<PasswordPreflightResult> PreflightAsync(Func<Task<GraphHttpClient>> openClient, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roles;
        IReadOnlyList<string> directoryRoles;
        try
        {
            using var client = await openClient();
            var token = await client.GetAccessTokenAsync(cancellationToken);
            roles = GraphAccessToken.ReadRoles(token);
            directoryRoles = GraphAccessToken.ReadDirectoryRoles(token);
        }
        catch (Exception ex) when (ex is HttpAuthenticationException or GraphRequestException or HttpRequestException or InvalidOperationException or ServerCertificateRejectedException)
        {
            return new PasswordPreflightResult
            {
                TargetDescription = "Microsoft Entra ID",
                Checks =
                [
                    PasswordPreflightCheckResult.Failed(PasswordPreflightCheck.Connection,
                        Pf(nameof(ConnectorMessages.PfGraphNoToken), ex is HttpAuthenticationException { Detail: { } detail } ? detail : (MessageArgument)ex.Message)),
                    PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Encryption, Pf(nameof(ConnectorMessages.PfGraphHttps))),
                    PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PasswordMechanism, Pf(nameof(ConnectorMessages.PfNotChecked))),
                    PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.ResetRights, Pf(nameof(ConnectorMessages.PfNotChecked))),
                    PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PolicyDiscovery, Pf(nameof(ConnectorMessages.PfGraphPolicySame)))
                ]
            };
        }

        return new PasswordPreflightResult
        {
            TargetDescription = "Microsoft Entra ID",
            Checks =
            [
                PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Connection, Pf(nameof(ConnectorMessages.PfGraphToken))),
                PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Encryption, Pf(nameof(ConnectorMessages.PfGraphHttps))),
                PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PasswordMechanism, Pf(nameof(ConnectorMessages.PfGraphMechanism))),
                ResetRights(roles, directoryRoles),
                PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PolicyDiscovery, Pf(nameof(ConnectorMessages.PfGraphPolicySameBanned)))
            ]
        };
    }

    private static PasswordPreflightCheckResult ResetRights(IReadOnlyList<string> roles, IReadOnlyList<string> directoryRoles)
    {
        if (!GraphPermissions.CanWritePasswordProfiles(roles))
        {
            return PasswordPreflightCheckResult.Failed(PasswordPreflightCheck.ResetRights,
                Pf(nameof(ConnectorMessages.PfGraphLacksPermission), GraphPermissions.UserPasswordProfileReadWrite));
        }

        var role = directoryRoles.Select(r => GraphPermissions.PasswordAdministratorRoles.GetValueOrDefault(r)).FirstOrDefault(name => name != null);
        if (role != null)
            return PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.ResetRights, Pf(nameof(ConnectorMessages.PfGraphHoldsRole), role));

        return PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.ResetRights, Pf(nameof(ConnectorMessages.PfGraphNoRole)));
    }

    private static JimMessage Pf(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ConnectorMessageCatalogues.Connectors, key, arguments);
}
