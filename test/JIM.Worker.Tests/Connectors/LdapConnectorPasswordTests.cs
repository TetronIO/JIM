// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Reflection;
using System.Text;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the LDAP Connector's password channel.
/// <para>
/// Password writes fail quietly when they are wrong. A unicodePwd value missing its quotation marks, or encoded
/// as UTF-8, is refused with a message that reads like a policy rejection; an account enabled before its password
/// lands is refused outright; a "must change at next sign-in" applied before the password is silently overwritten
/// by the password write itself. None of these are visible without asserting on the exact bytes and the exact
/// order of operations, which is what these tests do.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorPasswordTests
{
    private const string TestDn = "CN=Test User,OU=Users,DC=testdomain,DC=local";
    private const string TestPassword = "Correct-Horse-Battery-7";

    private Mock<ILdapOperationExecutor> _executor = null!;
    private List<DirectoryRequest> _sentRequests = null!;

    [SetUp]
    public void SetUp()
    {
        _executor = new Mock<ILdapOperationExecutor>();
        _sentRequests = [];
    }

    #region unicodePwd encoding

    /// <summary>
    /// The byte-level contract with Active Directory: the password surrounded by double quotation marks, encoded
    /// UTF-16 little-endian. Asserted byte by byte rather than by round-tripping through the same encoder the
    /// implementation uses, because a round-trip assertion would pass just as happily against UTF-8.
    /// </summary>
    [Test]
    public void EncodeUnicodePwd_WithAsciiPassword_ProducesQuotedUtf16LittleEndianBytes()
    {
        var result = LdapConnectorPassword.EncodeUnicodePwd("Ab1!");

        byte[] expected =
        [
            0x22, 0x00, // "
            0x41, 0x00, // A
            0x62, 0x00, // b
            0x31, 0x00, // 1
            0x21, 0x00, // !
            0x22, 0x00  // "
        ];

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void EncodeUnicodePwd_WithAnyPassword_EmitsNoByteOrderMark()
    {
        var result = LdapConnectorPassword.EncodeUnicodePwd(TestPassword);

        // A UTF-16 little-endian byte order mark is FF FE. Active Directory rejects the value if one is present.
        Assert.That(result[0], Is.EqualTo(0x22), "The first byte must be the opening quotation mark, not a byte order mark.");
        Assert.That(result[1], Is.EqualTo(0x00));
    }

    [Test]
    public void EncodeUnicodePwd_WithNonAsciiPassword_EncodesTheCharacterAsTwoBytes()
    {
        var result = LdapConnectorPassword.EncodeUnicodePwd("é");

        // U+00E9 little-endian is E9 00, wrapped in quotation marks. UTF-8 would give the two bytes C3 A9.
        Assert.That(result, Is.EqualTo(new byte[] { 0x22, 0x00, 0xE9, 0x00, 0x22, 0x00 }));
    }

    [Test]
    public void EncodeUnicodePwd_WithQuotationMarkInPassword_EncodesItAsAnOrdinaryCharacter()
    {
        var result = LdapConnectorPassword.EncodeUnicodePwd("a\"b");

        Assert.That(result, Is.EqualTo(Encoding.Unicode.GetBytes("\"a\"b\"")));
        Assert.That(result, Has.Length.EqualTo(10));
    }

    #endregion

    #region RFC 3062 request encoding

    /// <summary>
    /// PasswdModifyRequestValue ::= SEQUENCE { userIdentity [0] OCTET STRING, oldPasswd [1], newPasswd [2] }.
    /// Old password is deliberately absent: JIM sets passwords administratively and never knows the old value.
    /// </summary>
    [Test]
    public void BuildPasswordModifyRequestValue_WithShortValues_ProducesExpectedBerEncoding()
    {
        var result = LdapConnectorPassword.BuildPasswordModifyRequestValue("cn=a", "pw");

        byte[] expected =
        [
            0x30, 0x0A,                         // SEQUENCE, 10 bytes of content
            0x80, 0x04, (byte)'c', (byte)'n', (byte)'=', (byte)'a',  // [0] userIdentity "cn=a"
            0x82, 0x02, (byte)'p', (byte)'w'    // [2] newPasswd "pw"
        ];

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void BuildPasswordModifyRequestValue_WithNoOldPassword_OmitsTheOldPasswordField()
    {
        var result = LdapConnectorPassword.BuildPasswordModifyRequestValue("cn=a", "pw");

        Assert.That(result, Does.Not.Contain((byte)0x81), "The oldPasswd field [1] must not be present.");
    }

    /// <summary>
    /// Distinguished Names routinely exceed 127 bytes, which is the point at which BER switches from the short
    /// length form to the long one. Getting this wrong produces a request the directory cannot parse.
    /// </summary>
    [Test]
    public void BuildPasswordModifyRequestValue_WithLongDistinguishedName_UsesLongFormBerLength()
    {
        var longDn = "CN=" + new string('a', 200) + ",DC=testdomain,DC=local";

        var result = LdapConnectorPassword.BuildPasswordModifyRequestValue(longDn, "pw");

        var dnByteCount = Encoding.UTF8.GetByteCount(longDn);
        Assert.That(dnByteCount, Is.GreaterThan(0x7F), "This test is meaningless unless the Distinguished Name exceeds the short-form limit.");

        Assert.That(result[0], Is.EqualTo(0x30), "The outer structure is still a SEQUENCE.");

        // Content exceeds 127 bytes but not 255, so the sequence length is the one-byte long form: 0x81 then the length.
        Assert.That(result[1], Is.EqualTo(0x81), "A sequence between 128 and 255 bytes needs a one-byte long-form length.");
        Assert.That(result, Has.Length.EqualTo(3 + result[2]), "The declared length must match the bytes that follow it.");

        // The userIdentity field itself also needs a long-form length.
        Assert.That(result[3], Is.EqualTo(0x80), "The first field is still [0] userIdentity.");
        Assert.That(result[4], Is.EqualTo(0x81), "A Distinguished Name over 127 bytes needs a one-byte long-form length.");
        Assert.That(result[5], Is.EqualTo(dnByteCount));
    }

    /// <summary>
    /// The other side of the long form: content over 255 bytes needs two length bytes, big-endian. Getting the
    /// byte count or the ordering wrong here produces a request the directory silently cannot parse.
    /// </summary>
    [Test]
    public void BuildPasswordModifyRequestValue_WithVeryLongDistinguishedName_UsesTwoByteLongFormBerLength()
    {
        var longDn = "CN=" + new string('a', 400) + ",DC=testdomain,DC=local";

        var result = LdapConnectorPassword.BuildPasswordModifyRequestValue(longDn, "pw");

        var dnByteCount = Encoding.UTF8.GetByteCount(longDn);
        Assert.That(dnByteCount, Is.GreaterThan(0xFF), "This test is meaningless unless the Distinguished Name exceeds one length byte.");

        Assert.That(result[1], Is.EqualTo(0x82), "A sequence longer than 255 bytes needs a two-byte long-form length.");

        var contentLength = (result[2] << 8) | result[3];
        Assert.That(result, Has.Length.EqualTo(4 + contentLength), "The declared length must match the bytes that follow it.");

        Assert.That(result[4], Is.EqualTo(0x80));
        Assert.That(result[5], Is.EqualTo(0x82), "The Distinguished Name itself now needs two length bytes too.");
        Assert.That((result[6] << 8) | result[7], Is.EqualTo(dnByteCount));
    }

    #endregion

    #region userAccountControl bit handling

    private const int UacNormalAccount = 0x0200;
    private const int UacAccountDisable = 0x0002;
    private const int UacDontExpirePassword = 0x10000;

    [Test]
    public void ApplyUserAccountControlFlags_NeverExpires_SetsTheDontExpirePasswordBit()
    {
        var result = LdapConnectorPassword.ApplyUserAccountControlFlags(
            UacNormalAccount, PasswordExpiryBehaviour.NeverExpires, enableAccount: null);

        Assert.That(result & UacDontExpirePassword, Is.EqualTo(UacDontExpirePassword));
    }

    /// <summary>
    /// The important direction. An administrator moving an account off "never expires" must actually clear the
    /// flag; leaving it alone would silently ignore the choice they just made.
    /// </summary>
    [Test]
    public void ApplyUserAccountControlFlags_RequireChangeAtNextSignIn_ClearsAnExistingDontExpirePasswordBit()
    {
        var result = LdapConnectorPassword.ApplyUserAccountControlFlags(
            UacNormalAccount | UacDontExpirePassword, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, enableAccount: null);

        Assert.That(result & UacDontExpirePassword, Is.Zero);
    }

    [Test]
    public void ApplyUserAccountControlFlags_ExpiresAccordingToTargetPolicy_ClearsAnExistingDontExpirePasswordBit()
    {
        var result = LdapConnectorPassword.ApplyUserAccountControlFlags(
            UacNormalAccount | UacDontExpirePassword, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, enableAccount: null);

        Assert.That(result & UacDontExpirePassword, Is.Zero);
    }

    [Test]
    public void ApplyUserAccountControlFlags_EnableAccount_ClearsTheAccountDisableBit()
    {
        var result = LdapConnectorPassword.ApplyUserAccountControlFlags(
            UacNormalAccount | UacAccountDisable, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, enableAccount: true);

        Assert.That(result & UacAccountDisable, Is.Zero);
        Assert.That(result & UacNormalAccount, Is.EqualTo(UacNormalAccount));
    }

    [Test]
    public void ApplyUserAccountControlFlags_DisableAccount_SetsTheAccountDisableBit()
    {
        var result = LdapConnectorPassword.ApplyUserAccountControlFlags(
            UacNormalAccount, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, enableAccount: false);

        Assert.That(result & UacAccountDisable, Is.EqualTo(UacAccountDisable));
    }

    [Test]
    public void ApplyUserAccountControlFlags_WithNoEnabledStateRequested_LeavesTheAccountDisableBitUnchanged()
    {
        var disabled = LdapConnectorPassword.ApplyUserAccountControlFlags(
            UacNormalAccount | UacAccountDisable, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, enableAccount: null);
        var enabled = LdapConnectorPassword.ApplyUserAccountControlFlags(
            UacNormalAccount, PasswordExpiryBehaviour.RequireChangeAtNextSignIn, enableAccount: null);

        Assert.That(disabled & UacAccountDisable, Is.EqualTo(UacAccountDisable));
        Assert.That(enabled & UacAccountDisable, Is.Zero);
    }

    /// <summary>
    /// userAccountControl is a bitmask of unrelated account properties (smartcard required, delegation, and so on).
    /// A read-modify-write that clobbers them would silently reconfigure every account JIM touches.
    /// </summary>
    [Test]
    public void ApplyUserAccountControlFlags_WithUnrelatedFlagsSet_PreservesThem()
    {
        const int smartcardRequired = 0x40000;
        const int notDelegated = 0x100000;
        var current = UacNormalAccount | smartcardRequired | notDelegated;

        var result = LdapConnectorPassword.ApplyUserAccountControlFlags(
            current, PasswordExpiryBehaviour.NeverExpires, enableAccount: true);

        Assert.That(result & smartcardRequired, Is.EqualTo(smartcardRequired));
        Assert.That(result & notDelegated, Is.EqualTo(notDelegated));
        Assert.That(result & UacNormalAccount, Is.EqualTo(UacNormalAccount));
    }

    #endregion

    #region failure classification

    [Test]
    public void ClassifyFailure_ConstraintViolation_IsAPolicyRejection()
    {
        var result = LdapConnectorPassword.ClassifyFailure(ResultCode.ConstraintViolation, "Password fails quality checking policy");

        Assert.That(result, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
    }

    /// <summary>
    /// Active Directory reports the same rejection under more than one result code, but always embeds the Win32
    /// code. Classifying on the result code alone would park some rejections and endlessly retry others.
    /// </summary>
    [Test]
    public void ClassifyFailure_WithPasswordRestrictionsCodeInMessage_IsAPolicyRejectionWhateverTheResultCode()
    {
        const string adMessage = "0000052D: SvcErr: DSID-031A126C, problem 5003 (WILL_NOT_PERFORM), data 0";

        var result = LdapConnectorPassword.ClassifyFailure(ResultCode.UnwillingToPerform, adMessage);

        Assert.That(result, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
    }

    [Test]
    public void ClassifyFailure_NoSuchObject_IsTargetObjectNotFound()
    {
        Assert.That(LdapConnectorPassword.ClassifyFailure(ResultCode.NoSuchObject, null),
            Is.EqualTo(PasswordSetFailureReason.TargetObjectNotFound));
    }

    [Test]
    public void ClassifyFailure_InsufficientAccessRights_IsAConfigurationFault()
    {
        Assert.That(LdapConnectorPassword.ClassifyFailure(ResultCode.InsufficientAccessRights, null),
            Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
    }

    [Test]
    public void ClassifyFailure_ConfidentialityRequired_IsAConfigurationFault()
    {
        Assert.That(LdapConnectorPassword.ClassifyFailure(ResultCode.ConfidentialityRequired, null),
            Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
    }

    [Test]
    public void ClassifyFailure_Busy_IsTransient()
    {
        Assert.That(LdapConnectorPassword.ClassifyFailure(ResultCode.Busy, null),
            Is.EqualTo(PasswordSetFailureReason.Transient));
    }

    /// <summary>
    /// An unrecognised refusal must not be treated as transient, or JIM would retry it forever against a
    /// directory that is never going to change its answer.
    /// </summary>
    [Test]
    public void ClassifyFailure_UnrecognisedResultCode_IsAConfigurationFaultRatherThanTransient()
    {
        Assert.That(LdapConnectorPassword.ClassifyFailure(ResultCode.ProtocolError, null),
            Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
    }

    #endregion

    #region Active Directory end-to-end

    [Test]
    public async Task SetPasswordAsync_AgainstActiveDirectory_WritesTheQuotedUtf16PasswordToUnicodePwdAsync()
    {
        SetupActiveDirectory(currentUserAccountControl: 0x0202);

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory)
            .SetPasswordAsync(TestDn, TestPassword, new PasswordSetOptions(), CancellationToken.None);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var passwordModify = SingleModifyFor(LdapConnectorPassword.AttributeUnicodePwd);
        Assert.That(passwordModify.DistinguishedName, Is.EqualTo(TestDn));

        var modification = passwordModify.Modifications[0];
        Assert.That(modification.Operation, Is.EqualTo(DirectoryAttributeOperation.Replace));
        Assert.That(modification[0], Is.EqualTo(LdapConnectorPassword.EncodeUnicodePwd(TestPassword)));
    }

    /// <summary>
    /// Ordering, not just presence. Writing unicodePwd resets pwdLastSet to the time of the write, so a
    /// pwdLastSet = 0 sent first is discarded and the user is never prompted to change their password.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_RequireChangeAtNextSignIn_SetsPwdLastSetToZeroAfterThePasswordAsync()
    {
        SetupActiveDirectory(currentUserAccountControl: 0x0200);

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory).SetPasswordAsync(
            TestDn, TestPassword,
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn },
            CancellationToken.None);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var pwdLastSet = SingleModifyFor(LdapConnectorPassword.AttributePwdLastSet);
        Assert.That(pwdLastSet.Modifications[0][0], Is.EqualTo("0"));

        Assert.That(IndexOfModifyFor(LdapConnectorPassword.AttributeUnicodePwd),
            Is.LessThan(IndexOfModifyFor(LdapConnectorPassword.AttributePwdLastSet)),
            "pwdLastSet must be written after unicodePwd, because writing the password resets pwdLastSet.");
    }

    [Test]
    public async Task SetPasswordAsync_NeverExpires_SetsTheDontExpirePasswordBitAndDoesNotWritePwdLastSetAsync()
    {
        SetupActiveDirectory(currentUserAccountControl: 0x0200);

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory).SetPasswordAsync(
            TestDn, TestPassword,
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires },
            CancellationToken.None);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));

        var userAccountControl = SingleModifyFor(LdapConnectorPassword.AttributeUserAccountControl);
        Assert.That(userAccountControl.Modifications[0][0], Is.EqualTo((0x0200 | UacDontExpirePassword).ToString()));

        Assert.That(ModifiesFor(LdapConnectorPassword.AttributePwdLastSet), Is.Empty,
            "'Never expires' and 'must change at next sign-in' are mutually exclusive, so pwdLastSet must not be written.");
    }

    /// <summary>
    /// The provisioning sequence. Active Directory will not enable an account that does not already hold a
    /// policy-compliant password, so the enable has to come after the password write.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_EnablingTheAccount_ClearsTheDisableBitAfterThePasswordAsync()
    {
        SetupActiveDirectory(currentUserAccountControl: 0x0202);

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory).SetPasswordAsync(
            TestDn, TestPassword,
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn, EnableAccount = true },
            CancellationToken.None);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var userAccountControl = SingleModifyFor(LdapConnectorPassword.AttributeUserAccountControl);
        Assert.That(userAccountControl.Modifications[0][0], Is.EqualTo("512"), "0x0202 with the disable bit cleared is 0x0200.");

        Assert.That(IndexOfModifyFor(LdapConnectorPassword.AttributeUnicodePwd),
            Is.LessThan(IndexOfModifyFor(LdapConnectorPassword.AttributeUserAccountControl)),
            "The account cannot be enabled until it holds a compliant password.");
    }

    [Test]
    public async Task SetPasswordAsync_WhenThePolicyRejectsThePassword_ReportsAPolicyRejectionAndLeavesTheAccountAloneAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                _sentRequests.Add(request);
                return Task.FromResult<DirectoryResponse>(CreateResponse<ModifyResponse>(
                    ResultCode.ConstraintViolation,
                    "0000052D: AtrErr: DSID-03191083, #1: 0: 0000052D: DSID-03191083, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 9005a (unicodePwd)"));
            });

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory)
            .SetPasswordAsync(TestDn, TestPassword, new PasswordSetOptions(), CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
        Assert.That(_sentRequests, Has.Count.EqualTo(1), "A rejected password must not be followed by account-flag changes.");
    }

    /// <summary>
    /// Results are logged, recorded on Activities, and rendered in the portal, so a password value reaching one
    /// would leak it into all three at once.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_WhateverTheOutcome_NeverReturnsThePasswordAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                _sentRequests.Add(request);
                return Task.FromResult<DirectoryResponse>(CreateResponse<ModifyResponse>(
                    ResultCode.ConstraintViolation, $"rejected the value {TestPassword} for policy reasons"));
            });

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory)
            .SetPasswordAsync(TestDn, TestPassword, new PasswordSetOptions(), CancellationToken.None);

        // Even when the directory itself echoes the password back in its diagnostic, JIM must not carry it forward.
        Assert.That(result.ErrorMessage, Does.Not.Contain(TestPassword));
        Assert.That(result.ErrorMessage, Does.Contain("[password redacted]"),
            "The rest of the directory's diagnostic is still useful to an administrator, so redact the password rather than discarding the message.");
    }

    #endregion

    #region directories that are not Active Directory

    [Test]
    public async Task SetPasswordAsync_AgainstAGenericDirectory_UsesThePasswordModifyExtendedOperationAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                _sentRequests.Add(request);
                return Task.FromResult<DirectoryResponse>(CreateResponse<ExtendedResponse>(ResultCode.Success));
            });

        var result = await CreateChannel(LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: true).SetPasswordAsync(
            TestDn, TestPassword,
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy },
            CancellationToken.None);

        Assert.That(result.Success, Is.True, result.ErrorMessage);

        var extended = _sentRequests.OfType<ExtendedRequest>().Single();
        Assert.That(extended.RequestName, Is.EqualTo(LdapConnectorPassword.PasswordModifyExtensionOid));
        Assert.That(extended.RequestValue, Is.EqualTo(LdapConnectorPassword.BuildPasswordModifyRequestValue(TestDn, TestPassword)));

        Assert.That(_sentRequests.OfType<ModifyRequest>(), Is.Empty,
            "A directory that is not Active Directory must never have a password attribute written to it directly.");
    }

    /// <summary>
    /// A directory applies its configured hashing to the extended operation but stores a directly written
    /// userPassword verbatim. Refusing is the only safe answer; writing the attribute anyway would put a
    /// cleartext password into the directory.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_WhenTheExtendedOperationIsUnsupported_FailsRatherThanWritingUserPasswordAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                _sentRequests.Add(request);
                return Task.FromResult<DirectoryResponse>(CreateResponse<ExtendedResponse>(ResultCode.Success));
            });

        var result = await CreateChannel(LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: false)
            .SetPasswordAsync(TestDn, TestPassword, new PasswordSetOptions(), CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
        Assert.That(_sentRequests, Is.Empty, "Nothing at all should be sent to a directory JIM cannot set a password on safely.");
    }

    /// <summary>
    /// The requirement that an unsupported expiry state is reported rather than silently dropped. The password
    /// still lands, so this is a success carrying a caveat, not a failure.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_RequestingNeverExpiresOnAGenericDirectory_ReportsTheDowngradeAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                _sentRequests.Add(request);
                return Task.FromResult<DirectoryResponse>(CreateResponse<ExtendedResponse>(ResultCode.Success));
            });

        var result = await CreateChannel(LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: true).SetPasswordAsync(
            TestDn, TestPassword,
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires },
            CancellationToken.None);

        Assert.That(result.Success, Is.True, "The password itself was set, so this is not a failure.");
        Assert.That(result.ExpiryBehaviourHonoured, Is.False);
        Assert.That(result.AppliedExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy));
        Assert.That(result.ExpiryBehaviourWarning, Is.Not.Null.And.Contains("Never expires"));
    }

    [Test]
    public async Task SetPasswordAsync_RequestingTheHonourableStateOnAGenericDirectory_ReportsNoDowngradeAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync(CreateResponse<ExtendedResponse>(ResultCode.Success));

        var result = await CreateChannel(LdapDirectoryType.OpenLDAP, supportsPasswordModifyExtension: true).SetPasswordAsync(
            TestDn, TestPassword,
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy },
            CancellationToken.None);

        Assert.That(result.ExpiryBehaviourHonoured, Is.True);
        Assert.That(result.ExpiryBehaviourWarning, Is.Null);
    }

    #endregion

    #region connector-level behaviour

    [Test]
    public void LdapConnector_DeclaresSupportForSettingPasswords()
    {
        var connector = new LdapConnector();

        Assert.That(connector.SupportsPasswordSet, Is.True);
        Assert.That(connector.SupportedExpiryBehaviours, Is.EquivalentTo(new[]
        {
            PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
            PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
            PasswordExpiryBehaviour.NeverExpires
        }));
    }

    /// <summary>
    /// An unencrypted connection is a bad idea, not a refusal. Some deployments genuinely cannot offer TLS on
    /// their directory, and locking those sites out of password management entirely helps nobody; JIM warns
    /// instead and lets the administrator decide. This asserts the channel does not reject the configuration out
    /// of hand: it gets as far as trying to reach the directory, which is a different failure entirely.
    /// </summary>
    [Test]
    public void OpenPasswordConnection_WithoutSecureConnectionEnabled_DoesNotRefuseTheConfiguration()
    {
        var connector = new LdapConnector();
        var settings = new List<ConnectedSystemSettingValue>
        {
            new() { Setting = new ConnectorDefinitionSetting { Name = "Use Secure Connection (LDAPS)?" }, CheckboxValue = false },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Host" }, StringValue = "directory.invalid" },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Port" }, IntValue = 389 },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Connection Timeout" }, IntValue = 1 },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Username" }, StringValue = "svc-jim" },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Password" }, StringEncryptedValue = "not-a-real-password" },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Authentication Type" }, StringValue = "Simple" },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Maximum Retries" }, IntValue = 1 },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Retry Delay (ms)" }, IntValue = 1 }
        };

        var exception = Assert.Catch(() => connector.OpenPasswordConnection(settings));

        Assert.That(exception, Is.Not.Null, "Reaching a directory that does not exist should still fail.");
        Assert.That(exception, Is.Not.InstanceOf<InvalidSettingValuesException>(),
            "The settings themselves are complete and valid; only the connection attempt should fail.");
        Assert.That(exception!.Message, Does.Not.Contain("encrypted"),
            "The absence of LDAPS must not be what stops the channel opening.");
    }

    /// <summary>
    /// Active Directory refuses a password write unless the connection is encrypted or the bind is signed and
    /// sealed, and reports it with a result code that gives an administrator very little to go on. Naming the
    /// likely cause is the difference between an actionable failure and a puzzling one.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_RefusedOnAnUnencryptedConnection_NamesEncryptionAsTheLikelyFixAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync(CreateResponse<ModifyResponse>(ResultCode.ConfidentialityRequired, "00002028: LdapErr: DSID-0C090F5F"));

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory, isConnectionEncrypted: false)
            .SetPasswordAsync(TestDn, TestPassword, new PasswordSetOptions(), CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
        Assert.That(result.ErrorMessage, Does.Contain("LDAPS"));
    }

    /// <summary>
    /// The same refusal on an already-encrypted connection has some other cause, so pointing at encryption would
    /// send the administrator to fix something that is not broken.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_RefusedOnAnEncryptedConnection_DoesNotBlameEncryptionAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync(CreateResponse<ModifyResponse>(ResultCode.ConfidentialityRequired, "00002028: LdapErr: DSID-0C090F5F"));

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory, isConnectionEncrypted: true)
            .SetPasswordAsync(TestDn, TestPassword, new PasswordSetOptions(), CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Not.Contain("LDAPS"));
    }

    /// <summary>
    /// A policy rejection on an unencrypted connection is still a policy rejection. Appending an encryption note
    /// to it would point at the wrong problem.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_PolicyRejectionOnAnUnencryptedConnection_DoesNotBlameEncryptionAsync()
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .ReturnsAsync(CreateResponse<ModifyResponse>(ResultCode.ConstraintViolation, "Password fails quality checking policy"));

        var result = await CreateChannel(LdapDirectoryType.ActiveDirectory, isConnectionEncrypted: false)
            .SetPasswordAsync(TestDn, TestPassword, new PasswordSetOptions(), CancellationToken.None);

        Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
        Assert.That(result.ErrorMessage, Does.Not.Contain("LDAPS"));
    }

    [Test]
    public void SetPasswordAsync_WithoutOpeningTheChannel_Throws()
    {
        var connector = new LdapConnector();

        Assert.ThrowsAsync<InvalidOperationException>(() => connector.SetPasswordAsync(
            new ConnectedSystemObject(), TestPassword, new PasswordSetOptions(), CancellationToken.None));
    }

    #endregion

    #region helpers

    private LdapConnectorPassword CreateChannel(LdapDirectoryType directoryType, bool supportsPasswordModifyExtension = false, bool isConnectionEncrypted = true) =>
        new(_executor.Object, Log.Logger, directoryType, supportsPasswordModifyExtension, isConnectionEncrypted);

    /// <summary>
    /// Sets up an Active Directory that accepts every modification and reports the given userAccountControl when
    /// the entry is read back.
    /// </summary>
    private void SetupActiveDirectory(int currentUserAccountControl)
    {
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                _sentRequests.Add(request);

                DirectoryResponse response = request is SearchRequest
                    ? CreateSearchResponseWithUserAccountControl(currentUserAccountControl)
                    : CreateResponse<ModifyResponse>(ResultCode.Success);

                return Task.FromResult(response);
            });
    }

    private IEnumerable<ModifyRequest> ModifiesFor(string attributeName) =>
        _sentRequests.OfType<ModifyRequest>()
            .Where(r => r.Modifications.Cast<DirectoryAttributeModification>()
                .Any(m => string.Equals(m.Name, attributeName, StringComparison.OrdinalIgnoreCase)));

    private ModifyRequest SingleModifyFor(string attributeName)
    {
        var matches = ModifiesFor(attributeName).ToList();
        Assert.That(matches, Has.Count.EqualTo(1), $"Expected exactly one modification of '{attributeName}'.");
        return matches[0];
    }

    private int IndexOfModifyFor(string attributeName) => _sentRequests.IndexOf(SingleModifyFor(attributeName));

    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// Builds a DirectoryResponse subclass. They have an internal five-parameter constructor and no public way to
    /// create one, so tests have to go through reflection (the same approach the export tests take).
    /// </summary>
    private static T CreateResponse<T>(ResultCode resultCode, string? errorMessage = null) where T : DirectoryResponse =>
        (T)Activator.CreateInstance(typeof(T), NonPublicInstance, binder: null,
            args: ["", Array.Empty<DirectoryControl>(), resultCode, errorMessage ?? "", Array.Empty<Uri>()],
            culture: null)!;

    private static SearchResponse CreateSearchResponseWithUserAccountControl(int userAccountControl)
    {
        var attributes = (SearchResultAttributeCollection)Activator.CreateInstance(typeof(SearchResultAttributeCollection), nonPublic: true)!;
        typeof(SearchResultAttributeCollection)
            .GetMethod("Add", NonPublicInstance, [typeof(string), typeof(DirectoryAttribute)])!
            .Invoke(attributes, [
                LdapConnectorPassword.AttributeUserAccountControl,
                new DirectoryAttribute(LdapConnectorPassword.AttributeUserAccountControl, userAccountControl.ToString())
            ]);

        var entry = (SearchResultEntry)Activator.CreateInstance(typeof(SearchResultEntry), NonPublicInstance, binder: null,
            args: [TestDn, attributes], culture: null)!;

        var entries = (SearchResultEntryCollection)Activator.CreateInstance(typeof(SearchResultEntryCollection), nonPublic: true)!;
        typeof(SearchResultEntryCollection).GetMethod("Add", NonPublicInstance, [typeof(SearchResultEntry)])!
            .Invoke(entries, [entry]);

        var response = CreateResponse<SearchResponse>(ResultCode.Success);
        typeof(SearchResponse).GetMethod("set_Entries", NonPublicInstance)!.Invoke(response, [entries]);
        return response;
    }

    #endregion
}
