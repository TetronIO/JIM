// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Security.Cryptography;
using JIM.Application.Services;
using Microsoft.AspNetCore.DataProtection;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The dedicated protection purpose synchronised passwords are encrypted under (#1119, requirement 7).
/// <para>
/// Run against a real Data Protection provider rather than a mock, because the property under test is precisely
/// the one a mock cannot express: that a value protected under one purpose is unreadable under the other. A
/// shared purpose would mean a leaked or misused protector reached queued passwords and Connected System bind
/// credentials alike.
/// </para>
/// </summary>
[TestFixture]
public class PasswordProtectionPurposeTests
{
    private CredentialProtectionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        // Ephemeral keys: each fixture run gets its own key ring, and nothing is written to disk.
        _service = new CredentialProtectionService(new EphemeralDataProtectionProvider());
    }

    [Test]
    public void ProtectPassword_ThenUnprotectPassword_RoundTrips()
    {
        const string password = "Correct Horse Battery Staple 42!";

        var protectedValue = _service.ProtectPassword(password);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(protectedValue, Is.Not.EqualTo(password), "The stored value must not be the password.");
            Assert.That(protectedValue, Does.Not.Contain("Battery"),
                "No fragment of the password may survive into the stored value.");
            Assert.That(_service.UnprotectPassword(protectedValue), Is.EqualTo(password));
        }
    }

    [Test]
    public void ProtectPassword_UsesADistinctPrefixFromCredentialProtection()
    {
        // The prefix is how a stored value declares which protector can read it. Sharing one would make a
        // credential and a queued password indistinguishable on inspection.
        var protectedPassword = _service.ProtectPassword("a-password");
        var protectedCredential = _service.Protect("a-credential");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.IsPasswordProtected(protectedPassword), Is.True);
            Assert.That(_service.IsProtected(protectedPassword), Is.False,
                "A password-protected value must not be mistaken for a credential-protected one.");
            Assert.That(_service.IsPasswordProtected(protectedCredential), Is.False,
                "A credential-protected value must not be mistaken for a password-protected one.");
        }
    }

    [Test]
    public void UnprotectPassword_WithAValueProtectedUnderTheCredentialPurpose_Throws()
    {
        // The isolation this whole purpose exists for: the two protectors cannot read each other's output.
        var protectedCredential = _service.Protect("a-credential");

        Assert.That(() => _service.UnprotectPassword(protectedCredential),
            Throws.InstanceOf<CryptographicException>().Or.InstanceOf<FormatException>());
    }

    [Test]
    public void Unprotect_WithAValueProtectedUnderThePasswordPurpose_Throws()
    {
        // Not symmetrical with the test above by accident. Unprotect passes an unrecognised value straight back
        // as plain text, to support credentials stored before encryption existed, so without an explicit refusal
        // a password payload reaching it would be handed back as ciphertext and then sent to a target system as
        // though it were the credential.
        var protectedPassword = _service.ProtectPassword("a-password");

        Assert.That(() => _service.Unprotect(protectedPassword), Throws.InstanceOf<CryptographicException>());
    }

    [Test]
    public void ProtectPassword_CalledTwiceOnTheSameValue_DoesNotDoubleEncrypt()
    {
        var once = _service.ProtectPassword("a-password");
        var twice = _service.ProtectPassword(once);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(twice, Is.EqualTo(once));
            Assert.That(_service.UnprotectPassword(twice), Is.EqualTo("a-password"));
        }
    }

    [Test]
    public void ProtectPassword_WithNullOrEmpty_PassesThrough()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.ProtectPassword(null), Is.Null);
            Assert.That(_service.ProtectPassword(string.Empty), Is.Empty);
            Assert.That(_service.UnprotectPassword(null), Is.Null);
            Assert.That(_service.UnprotectPassword(string.Empty), Is.Empty);
        }
    }

    [Test]
    public void UnprotectPassword_WithAnUnprotectedValue_Throws()
    {
        // Deliberately unlike Unprotect, which returns unprefixed input as-is to support credentials stored
        // before encryption existed. No queued password has ever been stored in clear, so a value arriving
        // without the prefix is corruption or tampering, and returning it would hand the caller something it
        // would then transmit to a directory as though JIM had encrypted it.
        Assert.That(() => _service.UnprotectPassword("not-encrypted"), Throws.InstanceOf<CryptographicException>());
    }

    [Test]
    public void ProtectPassword_OnTheSameValueTwice_ProducesDifferentCiphertext()
    {
        // Non-deterministic encryption: two rows holding the same password must not be recognisably equal.
        var first = _service.ProtectPassword("a-password");
        var second = _service.ProtectPassword("a-password" + string.Empty);

        Assert.That(first, Is.Not.EqualTo(second));
    }
}
