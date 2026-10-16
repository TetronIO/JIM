// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Sets passwords on objects in an LDAP directory.
/// <para>
/// Two mechanisms, chosen by directory type. Active Directory has no support for the standard password
/// mechanism and requires its own proprietary attribute; everything else uses the RFC 3062 Password Modify
/// extended operation. Neither one writes a password attribute directly.
/// </para>
/// <para>
/// No method on this class ever logs, returns, or persists the password value.
/// </para>
/// </summary>
internal class LdapConnectorPassword
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;
    private readonly LdapDirectoryType _directoryType;
    private readonly bool _supportsPasswordModifyExtension;
    private readonly bool _isConnectionEncrypted;

    internal LdapConnectorPassword(
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

    /// <summary>
    /// Whether this directory is one where passwords are set through Active Directory's proprietary
    /// unicodePwd attribute rather than the RFC 3062 extended operation.
    /// </summary>
    private bool IsActiveDirectory =>
        _directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD;

    /// <summary>
    /// Sets the password on a directory entry, then applies the requested expiry behaviour and, where asked,
    /// enables the account.
    /// <para>
    /// The order matters and is not interchangeable. Active Directory refuses to enable an account that does
    /// not already hold a policy-compliant password, so the password has to land before the enable. Writing
    /// unicodePwd also resets pwdLastSet to the current time, so "must change at next sign-in" has to be
    /// applied after the password, never before, or it is silently overwritten.
    /// </para>
    /// </summary>
    internal async Task<PasswordSetResult> SetPasswordAsync(
        string distinguishedName,
        string password,
        PasswordSetOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(distinguishedName))
            return PasswordSetResult.Failed(PasswordSetFailureReason.TargetObjectNotFound,
                "The object has no Distinguished Name, so there is nothing to set a password on.");

        if (string.IsNullOrEmpty(password))
            return PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault,
                "No password was supplied.");

        cancellationToken.ThrowIfCancellationRequested();

        var failure = IsActiveDirectory
            ? await SetActiveDirectoryPasswordAsync(distinguishedName, password)
            : await SetPasswordViaExtendedOperationAsync(distinguishedName, password);

        if (failure != null)
            return failure;

        // The password is set. From here, a failure leaves the account holding the right password but the wrong
        // expiry or enabled state, which is a materially different situation from the password not landing at all.
        cancellationToken.ThrowIfCancellationRequested();
        return await ApplyPostPasswordStateAsync(distinguishedName, options, password);
    }

    /// <summary>
    /// Removes the password from a directory's diagnostic message.
    /// <para>
    /// A directory is under no obligation to keep the value out of its own error text, and some do echo the
    /// rejected value back. JIM puts these messages into service logs, Activities, and the administration portal,
    /// so passing one straight through would leak the password into all three at once. Redacting here, at the one
    /// point where the message and the password are both in scope, is the only place it can be done reliably.
    /// </para>
    /// </summary>
    private static string Redact(string message, string password)
    {
        if (string.IsNullOrEmpty(password))
            return message;

        // Withheld whole rather than patched. Cutting the password out of the message and keeping the rest
        // depends on the echo matching exactly: a directory that uppercased it, or quoted only part of it,
        // would leave the credential in a message JIM then hands to a caller and writes to a log. Directories
        // do not in practice echo a submitted password, so this costs nothing in the ordinary case and fails
        // safe in the one that matters. The result code and JIM's own classification still say what happened.
        return message.Contains(password, StringComparison.OrdinalIgnoreCase)
            ? "[the directory's message is withheld because it contained the password]"
            : message;
    }

    /// <summary>
    /// Writes the password to Active Directory's unicodePwd attribute. Returns null on success, or the failure
    /// to report.
    /// </summary>
    private async Task<PasswordSetResult?> SetActiveDirectoryPasswordAsync(string distinguishedName, string password)
    {
        var modification = new DirectoryAttributeModification
        {
            Name = AttributeUnicodePwd,
            Operation = DirectoryAttributeOperation.Replace
        };
        modification.Add(EncodeUnicodePwd(password));

        return await SendAndClassifyAsync(new ModifyRequest(distinguishedName, modification),
            $"set the password on '{distinguishedName}'", password);
    }

    /// <summary>
    /// Encodes a password the way Active Directory requires for a unicodePwd write: the password surrounded by
    /// double quotation marks, encoded as UTF-16 little-endian, with no byte order mark.
    /// <para>
    /// This is easy to get subtly wrong and the failure is quiet. Omit the quotation marks and the directory
    /// returns a constraint violation that reads like a policy rejection; send a UTF-8 string and the write is
    /// simply rejected. The encoding therefore belongs to the Connector, not to an administrator writing an
    /// Attribute Flow, which is one of the reasons unicodePwd is on JIM's credential-attribute denylist.
    /// </para>
    /// </summary>
    internal static byte[] EncodeUnicodePwd(string password)
    {
        // Encoding.Unicode is UTF-16 little-endian, and GetBytes never emits a preamble.
        return Encoding.Unicode.GetBytes($"\"{password}\"");
    }

    /// <summary>
    /// Sets the password using the RFC 3062 Password Modify extended operation.
    /// <para>
    /// JIM deliberately does not write the userPassword attribute directly. A directory applies its configured
    /// password hashing to the extended operation, but stores a directly written userPassword value verbatim, so
    /// a plain attribute write is how cleartext passwords end up sitting in a directory. Refusing to do it is the
    /// right default for the environments JIM is deployed into.
    /// </para>
    /// </summary>
    private async Task<PasswordSetResult?> SetPasswordViaExtendedOperationAsync(string distinguishedName, string password)
    {
        if (!_supportsPasswordModifyExtension)
            return PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault,
                "This directory does not advertise support for the LDAP Password Modify extended operation (RFC 3062), " +
                "which is the only way JIM will set a password on a directory that is not Active Directory. Writing the " +
                "userPassword attribute directly would store the password without the directory's configured hashing " +
                "applied to it. Enable the extended operation on the directory, then retry.");

        var request = new ExtendedRequest(PasswordModifyExtensionOid)
        {
            RequestValue = BuildPasswordModifyRequestValue(distinguishedName, password)
        };

        return await SendAndClassifyAsync(request, $"set the password on '{distinguishedName}'", password);
    }

    /// <summary>
    /// BER-encodes the request value for the RFC 3062 Password Modify extended operation:
    /// <code>
    /// PasswdModifyRequestValue ::= SEQUENCE {
    ///     userIdentity    [0] OCTET STRING OPTIONAL,
    ///     oldPasswd       [1] OCTET STRING OPTIONAL,
    ///     newPasswd       [2] OCTET STRING OPTIONAL }
    /// </code>
    /// The old password is deliberately omitted: JIM sets passwords as an administrator and never knows the
    /// previous value.
    /// </summary>
    internal static byte[] BuildPasswordModifyRequestValue(string distinguishedName, string newPassword)
    {
        var content = new List<byte>();
        WriteTaggedOctetString(content, TagUserIdentity, Encoding.UTF8.GetBytes(distinguishedName));
        WriteTaggedOctetString(content, TagNewPassword, Encoding.UTF8.GetBytes(newPassword));

        var encoded = new List<byte> { TagSequence };
        WriteBerLength(encoded, content.Count);
        encoded.AddRange(content);
        return [.. encoded];
    }

    private static void WriteTaggedOctetString(List<byte> buffer, byte tag, byte[] value)
    {
        buffer.Add(tag);
        WriteBerLength(buffer, value.Length);
        buffer.AddRange(value);
    }

    /// <summary>
    /// Writes a BER length. Lengths below 128 use the short form (a single byte); anything longer uses the long
    /// form, where the first byte carries the count of subsequent big-endian length bytes. Distinguished Names
    /// and passwords both routinely exceed 127 bytes, so the long form is not a theoretical case.
    /// </summary>
    private static void WriteBerLength(List<byte> buffer, int length)
    {
        if (length < 0x80)
        {
            buffer.Add((byte)length);
            return;
        }

        var lengthBytes = new Stack<byte>();
        var remaining = length;
        while (remaining > 0)
        {
            lengthBytes.Push((byte)(remaining & 0xFF));
            remaining >>= 8;
        }

        buffer.Add((byte)(0x80 | lengthBytes.Count));
        buffer.AddRange(lengthBytes);
    }

    /// <summary>
    /// Applies the requested expiry behaviour and enabled state once the password is in place, and reports what the
    /// account's password now does about expiry.
    /// <para>
    /// "Password never expires" is applied only where the caller did not ask for it to be left as it is (#2159): where
    /// something else owns it, it stays as the account has it. "Must change at next sign-in" belongs to the password
    /// and is written whatever happens to the flag, after the flag, so that a delivery that leaves the flag set says so:
    /// Active Directory ignores pwdLastSet = 0 on an account whose password never expires.
    /// </para>
    /// </summary>
    private async Task<PasswordSetResult> ApplyPostPasswordStateAsync(string distinguishedName, PasswordSetOptions options, string password)
    {
        if (!IsActiveDirectory)
            return BuildNonActiveDirectoryResult(options);

        var (failure, readValue, finalValue) = await ApplyUserAccountControlAsync(distinguishedName, options, password);
        if (failure != null)
            return failure;

        if (options.ExpiryBehaviour == PasswordExpiryBehaviour.RequireChangeAtNextSignIn)
        {
            var modification = new DirectoryAttributeModification
            {
                Name = AttributePwdLastSet,
                Operation = DirectoryAttributeOperation.Replace
            };
            modification.Add(PwdLastSetMustChange);

            var pwdLastSetFailure = await SendAndClassifyAsync(new ModifyRequest(distinguishedName, modification),
                $"require a password change at next sign-in on '{distinguishedName}'", password);

            if (pwdLastSetFailure != null)
                return pwdLastSetFailure;
        }

        var neverExpiresNow = HasDontExpirePassword(finalValue);
        var result = options.ExpiryBehaviour == PasswordExpiryBehaviour.RequireChangeAtNextSignIn && neverExpiresNow
            ? PasswordSetResult.SucceededWithExpiryDowngrade(PasswordExpiryBehaviour.NeverExpires,
                "A change at the next sign-in was asked for and set, but the account has \"Password never expires\" set, which JIM " +
                "was told to leave as it is, and Active Directory does not ask for a new password while that flag is set.")
            : PasswordSetResult.Succeeded(DescribeAppliedExpiry(finalValue, options.ExpiryBehaviour));

        result.PasswordNeverExpiresChanged = readValue.HasValue && finalValue.HasValue && HasDontExpirePassword(readValue) != neverExpiresNow;
        return result;
    }

    /// <summary>
    /// What the account's password does about expiry, given the userAccountControl it was left with (null where the
    /// object has none) and the behaviour asked for: "never expires" wherever the flag is set, whatever was asked;
    /// otherwise what was asked, bar Never expires itself, which without the flag means the directory's policy.
    /// </summary>
    internal static PasswordExpiryBehaviour DescribeAppliedExpiry(int? finalUserAccountControl, PasswordExpiryBehaviour requested)
    {
        if (HasDontExpirePassword(finalUserAccountControl))
            return PasswordExpiryBehaviour.NeverExpires;

        return requested == PasswordExpiryBehaviour.NeverExpires
            ? PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy
            : requested;
    }

    private static bool HasDontExpirePassword(int? userAccountControl) =>
        userAccountControl is { } value && (value & LdapConnectorConstants.UAC_DONT_EXPIRE_PASSWORD) != 0;

    /// <summary>
    /// Reports what a directory that is not Active Directory could actually honour.
    /// <para>
    /// Expiry on these directories is governed by whichever password policy applies to the entry, and there is
    /// no portable per-entry override, so anything other than "expires according to the target's policy" is
    /// reported as a downgrade rather than quietly ignored. The password itself is set either way, so this is a
    /// success with a caveat. Never expires that the caller asked to be left as it is (#2159) was not asked to be
    /// applied, so it is no downgrade.
    /// </para>
    /// </summary>
    private PasswordSetResult BuildNonActiveDirectoryResult(PasswordSetOptions options)
    {
        var applied = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy;
        var asked = options.LeavePasswordNeverExpiresAsIs && options.ExpiryBehaviour == PasswordExpiryBehaviour.NeverExpires
            ? applied
            : options.ExpiryBehaviour;

        if (asked == applied && options.EnableAccount != true)
            return PasswordSetResult.Succeeded(applied);

        var caveats = new List<string>();

        if (asked != applied)
            caveats.Add($"'{DescribeExpiryBehaviour(asked)}' is an Active Directory behaviour with no " +
                        $"portable equivalent on this directory, so the password will expire according to whichever password " +
                        $"policy the directory applies to the account.");

        if (options.EnableAccount == true)
            caveats.Add("Enabling the account is an Active Directory operation; this directory has no equivalent flag, " +
                        "so the account's enabled state was left unchanged.");

        return PasswordSetResult.SucceededWithExpiryDowngrade(applied, string.Join(" ", caveats));
    }

    /// <summary>
    /// Reads the entry's current userAccountControl, applies the requested flags, and writes it back if the value
    /// changed. Returns the failure to report, or null on success; with the value read and the value the account was
    /// left with (both null for an object that has no userAccountControl).
    /// </summary>
    private async Task<(PasswordSetResult? Failure, int? ReadValue, int? FinalValue)> ApplyUserAccountControlAsync(
        string distinguishedName, PasswordSetOptions options, string password)
    {
        var searchRequest = new SearchRequest(distinguishedName, "(objectClass=*)", SearchScope.Base, AttributeUserAccountControl);

        SearchResponse searchResponse;
        try
        {
            searchResponse = (SearchResponse)await _executor.SendRequestAsync(searchRequest);
        }
        catch (DirectoryOperationException ex)
        {
            return (BuildFailure(ex.Response?.ResultCode ?? ResultCode.Other, ex.Message,
                $"read the current account flags on '{distinguishedName}'", password), null, null);
        }
        catch (LdapException ex)
        {
            return (PasswordSetResult.Failed(PasswordSetFailureReason.Transient,
                $"The password was set, but JIM could not read the current account flags on '{distinguishedName}' to apply " +
                $"the expiry and enabled state: {Redact(ex.Message, password)}"), null, null);
        }

        if (searchResponse.Entries.Count == 0)
            return (PasswordSetResult.Failed(PasswordSetFailureReason.TargetObjectNotFound,
                $"The password was set, but '{distinguishedName}' could not be read back to apply the expiry and enabled state."), null, null);

        var currentValue = ParseUserAccountControl(searchResponse.Entries[0]);
        if (currentValue == null)
        {
            // No userAccountControl means this object type has no account flags (a contact, for instance). The password
            // is set; there is simply nothing further to apply, which matters only where something was asked of a flag.
            var flagAsked = options.EnableAccount == true ||
                            (options.ExpiryBehaviour == PasswordExpiryBehaviour.NeverExpires && !options.LeavePasswordNeverExpiresAsIs);
            return flagAsked
                ? (PasswordSetResult.Failed(PasswordSetFailureReason.UnsupportedOperation,
                    $"The password was set, but '{distinguishedName}' has no userAccountControl attribute, so the requested " +
                    $"expiry and enabled state cannot be applied to it."), null, null)
                : (null, null, null);
        }

        var updatedValue = ApplyUserAccountControlFlags(currentValue.Value, options.ExpiryBehaviour, options.EnableAccount,
            options.LeavePasswordNeverExpiresAsIs);

        // A delivery that changes nothing sends nothing, so the compare-and-swap below (#2161) is never reached for it.
        if (updatedValue == currentValue.Value)
            return (null, currentValue, currentValue);

        var swapFailure = await SwapUserAccountControlAsync(distinguishedName, currentValue.Value, updatedValue, password);
        return swapFailure != null
            ? (swapFailure, currentValue, null)
            : (null, currentValue, updatedValue);
    }

    /// <summary>
    /// Writes the account's flags only if they are still the ones read (#2161): the value read is deleted and the new
    /// one added in a single request, which the directory applies whole or not at all, and refuses with
    /// noSuchAttribute when the value read is no longer there. The Password Delivery Service runs beside the export
    /// run, so an export can change the flags between the read and this write (a leaver's disable, say), and a
    /// Replace computed from the stale read would overwrite it, enabling the leaver.
    /// <para>
    /// On that refusal the flags are left as the export left them and the delivery is reported as a transient failure,
    /// rather than re-read and swapped again here: whether this password should enable the account is the engine's
    /// decision, taken afresh at the next attempt from what the export recorded, and re-applying this attempt's
    /// decision over the export's value would undo it all the same. The password itself is already set.
    /// </para>
    /// </summary>
    private async Task<PasswordSetResult?> SwapUserAccountControlAsync(string distinguishedName, int readValue, int newValue, string password)
    {
        var expected = new DirectoryAttributeModification
        {
            Name = AttributeUserAccountControl,
            Operation = DirectoryAttributeOperation.Delete
        };
        expected.Add(readValue.ToString(CultureInfo.InvariantCulture));

        var replacement = new DirectoryAttributeModification
        {
            Name = AttributeUserAccountControl,
            Operation = DirectoryAttributeOperation.Add
        };
        replacement.Add(newValue.ToString(CultureInfo.InvariantCulture));

        var operationDescription = $"apply the account flags on '{distinguishedName}'";
        ResultCode resultCode;
        string? errorMessage;
        try
        {
            var response = await _executor.SendRequestAsync(new ModifyRequest(distinguishedName, expected, replacement));
            resultCode = response.ResultCode;
            errorMessage = response.ErrorMessage;
        }
        catch (DirectoryOperationException ex)
        {
            resultCode = ex.Response?.ResultCode ?? ResultCode.Other;
            errorMessage = ex.Message;
        }
        catch (LdapException ex)
        {
            return PasswordSetResult.Failed(PasswordSetFailureReason.Transient,
                $"JIM could not {operationDescription}: {Redact(ex.Message, password)}");
        }

        if (resultCode == ResultCode.Success)
            return null;

        if (resultCode == ResultCode.NoSuchAttribute)
        {
            _logger.Information("LdapConnectorPassword: The account flags on {DistinguishedName} changed between JIM reading them ({ReadValue}) and writing {NewValue}; " +
                "they are left as they are, and the delivery is tried again",
                LogSanitiser.Sanitise(distinguishedName), readValue, newValue);
            return PasswordSetResult.Failed(PasswordSetFailureReason.Transient,
                $"The password was set, but the account flags on '{distinguishedName}' changed while JIM was applying the expiry and enabled state, " +
                "most likely because an export wrote them at the same moment. JIM left them as they are and tries again.");
        }

        return BuildFailure(resultCode, errorMessage, operationDescription, password);
    }

    /// <summary>
    /// Applies the expiry behaviour and enabled state to an Active Directory userAccountControl value.
    /// <para>
    /// "Must change at next sign-in" and "never expires" contradict each other in Active Directory, which is why
    /// JIM models expiry as one tri-state choice. Where the caller owns "Password never expires", selecting either of
    /// the expiring states therefore has to clear DONT_EXPIRE_PASSWORD, not merely leave it alone: an account that
    /// already carries the flag would otherwise keep it and silently ignore the choice. Where something else owns it
    /// (<paramref name="leavePasswordNeverExpiresAsIs"/>, #2159), the flag is left exactly as it is; the enabled state is
    /// still applied.
    /// </para>
    /// </summary>
    internal static int ApplyUserAccountControlFlags(int currentValue, PasswordExpiryBehaviour expiryBehaviour, bool? enableAccount,
        bool leavePasswordNeverExpiresAsIs = false)
    {
        var value = currentValue;
        if (!leavePasswordNeverExpiresAsIs)
            value = expiryBehaviour == PasswordExpiryBehaviour.NeverExpires
                ? value | LdapConnectorConstants.UAC_DONT_EXPIRE_PASSWORD
                : value & ~LdapConnectorConstants.UAC_DONT_EXPIRE_PASSWORD;

        if (enableAccount == true)
            value &= ~LdapConnectorConstants.UAC_ACCOUNTDISABLE;
        else if (enableAccount == false)
            value |= LdapConnectorConstants.UAC_ACCOUNTDISABLE;

        return value;
    }

    private static int? ParseUserAccountControl(SearchResultEntry entry)
    {
        var attribute = entry.Attributes[AttributeUserAccountControl];
        if (attribute == null || attribute.Count == 0)
            return null;

        return int.TryParse(attribute[0]?.ToString(), out var value) ? value : null;
    }

    /// <summary>
    /// Sends a request and converts anything other than success into a classified failure. Returns null when the
    /// operation succeeded.
    /// </summary>
    private async Task<PasswordSetResult?> SendAndClassifyAsync(DirectoryRequest request, string operationDescription, string password)
    {
        try
        {
            var response = await _executor.SendRequestAsync(request);
            if (response.ResultCode == ResultCode.Success)
                return null;

            return BuildFailure(response.ResultCode, response.ErrorMessage, operationDescription, password);
        }
        catch (DirectoryOperationException ex)
        {
            return BuildFailure(ex.Response?.ResultCode ?? ResultCode.Other, ex.Message, operationDescription, password);
        }
        catch (LdapException ex)
        {
            // An LdapException without a result code is a transport-level failure: the connection dropped, the
            // server went away, the operation timed out. Those are worth retrying as-is.
            return PasswordSetResult.Failed(PasswordSetFailureReason.Transient,
                $"JIM could not {operationDescription}: {Redact(ex.Message, password)}");
        }
    }

    private PasswordSetResult BuildFailure(ResultCode resultCode, string? errorMessage, string operationDescription, string password)
    {
        var reason = ClassifyFailure(resultCode, errorMessage);
        var detail = Redact(string.IsNullOrWhiteSpace(errorMessage) ? resultCode.ToString() : errorMessage, password);

        _logger.Warning("LdapConnectorPassword: Could not {Operation}. Result code {ResultCode}, classified as {Reason}. {Detail}",
            LogSanitiser.Sanitise(operationDescription), resultCode, reason, LogSanitiser.Sanitise(detail));

        var message = $"JIM could not {operationDescription}: {detail}";
        if (IsLikelyCausedByAnUnencryptedConnection(resultCode))
            message += " This Connected System is not using an encrypted connection, and directories commonly refuse " +
                       "password operations over one. Enabling LDAPS on the Connected System is the most likely fix.";

        return PasswordSetResult.Failed(reason, message);
    }

    /// <summary>
    /// Whether a refusal is one that an unencrypted connection would explain.
    /// <para>
    /// Active Directory rejects a password write unless the connection is encrypted or the bind is signed and
    /// sealed, and it reports that as a fairly opaque result code. Naming the likely cause turns a puzzling
    /// failure into an actionable one, but only where the code actually fits: appending it to a policy rejection
    /// would send an administrator off to fix the wrong thing.
    /// </para>
    /// </summary>
    private bool IsLikelyCausedByAnUnencryptedConnection(ResultCode resultCode)
    {
        if (_isConnectionEncrypted)
            return false;

        return resultCode is ResultCode.ConfidentialityRequired
            or ResultCode.StrongAuthRequired
            or ResultCode.InappropriateAuthentication
            or ResultCode.UnwillingToPerform;
    }

    /// <summary>
    /// Classifies a directory's refusal, which is what decides whether the work is retried, retried only after an
    /// administrator changes something, or parked because retrying the same value can never succeed.
    /// <para>
    /// The unknown case deliberately falls to a configuration fault rather than a transient one. Treating an
    /// unrecognised refusal as transient would retry it forever against a directory that is never going to
    /// change its mind.
    /// </para>
    /// </summary>
    internal static PasswordSetFailureReason ClassifyFailure(ResultCode resultCode, string? errorMessage)
    {
        // Active Directory reports a password policy rejection as a constraint violation carrying the Win32 code
        // ERROR_PASSWORD_RESTRICTIONS, and reports the same condition as unwillingToPerform in some cases. The
        // code in the diagnostic message is the reliable signal, so it is checked ahead of the result code.
        if (errorMessage != null && errorMessage.Contains(LdapConnectorConstants.AD_ERROR_PASSWORD_RESTRICTIONS, StringComparison.OrdinalIgnoreCase))
            return PasswordSetFailureReason.PolicyRejection;

        return resultCode switch
        {
            // Both Active Directory and directories running a password policy overlay report a value that fails
            // the policy as a constraint violation.
            ResultCode.ConstraintViolation => PasswordSetFailureReason.PolicyRejection,
            ResultCode.NoSuchObject => PasswordSetFailureReason.TargetObjectNotFound,
            ResultCode.InsufficientAccessRights => PasswordSetFailureReason.ConfigurationFault,
            ResultCode.StrongAuthRequired => PasswordSetFailureReason.ConfigurationFault,
            ResultCode.ConfidentialityRequired => PasswordSetFailureReason.ConfigurationFault,
            ResultCode.InappropriateAuthentication => PasswordSetFailureReason.ConfigurationFault,
            ResultCode.UnwillingToPerform => PasswordSetFailureReason.ConfigurationFault,
            ResultCode.Busy => PasswordSetFailureReason.Transient,
            ResultCode.Unavailable => PasswordSetFailureReason.Transient,
            ResultCode.TimeLimitExceeded => PasswordSetFailureReason.Transient,
            ResultCode.OperationsError => PasswordSetFailureReason.Transient,
            _ => PasswordSetFailureReason.ConfigurationFault
        };
    }

    private static string DescribeExpiryBehaviour(PasswordExpiryBehaviour behaviour) => behaviour switch
    {
        PasswordExpiryBehaviour.RequireChangeAtNextSignIn => "Require a change at next sign-in",
        PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy => "Expires according to the target's policy",
        PasswordExpiryBehaviour.NeverExpires => "Never expires",
        _ => behaviour.ToString()
    };

    #region constants
    /// <summary>
    /// The Active Directory attribute a password is written to. Never readable, and write-only over an encrypted
    /// connection.
    /// </summary>
    internal const string AttributeUnicodePwd = "unicodePwd";

    internal const string AttributeUserAccountControl = "userAccountControl";

    internal const string AttributePwdLastSet = "pwdLastSet";

    /// <summary>
    /// Writing zero to pwdLastSet is how Active Directory expresses "the user must choose a new password the next
    /// time they sign in".
    /// </summary>
    internal const string PwdLastSetMustChange = "0";

    /// <summary>
    /// RFC 3062 LDAP Password Modify extended operation.
    /// </summary>
    internal const string PasswordModifyExtensionOid = "1.3.6.1.4.1.4203.1.11.1";

    private const byte TagSequence = 0x30;

    /// <summary>Context-specific primitive tag [0], the userIdentity field.</summary>
    private const byte TagUserIdentity = 0x80;

    /// <summary>Context-specific primitive tag [2], the newPasswd field.</summary>
    private const byte TagNewPassword = 0x82;
    #endregion
}
