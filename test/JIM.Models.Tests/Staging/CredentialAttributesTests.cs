// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Pins the credential-attribute denylist and, just as importantly, the boundary between the two behaviours it
/// drives: a name on the denylist is <em>blocked</em> (JIM refuses to import it, manage it, or let an Attribute Flow
/// touch it), whereas a merely credential-looking name is <em>warned</em> about and nothing more. The warning
/// heuristic is deliberately loose and will flag ordinary directory attributes such as <c>pwdLastSet</c>; these
/// tests exist so that looseness can never quietly graduate into blocking a legitimate attribute.
/// </summary>
[TestFixture]
public class CredentialAttributesTests
{
    /// <summary>
    /// The denylist as the rest of JIM expects it. Duplicated here on purpose: if somebody adds or removes an entry
    /// in the production list, this list must be changed too, which forces the decision to be a conscious one.
    /// </summary>
    private static readonly string[] ExpectedDeniedNames =
    [
        "unicodePwd",
        "userPassword",
        "dBCSPwd",
        "ntPwdHistory",
        "lmPwdHistory",
        "supplementalCredentials",
        "unixUserPassword",
        "msDS-ManagedPassword"
    ];

    /// <summary>
    /// Names the warning heuristic is expected to flag even though they carry no credential material. Accepted
    /// cost of a substring match; they must warn, never block.
    /// </summary>
    private static readonly string[] KnownWarningFalsePositives =
    [
        "pwdLastSet",
        "pwdLastSetTime",
        "badPwdCount",
        "pwdProperties",
        "passwordHistoryLength"
    ];

    private static IEnumerable<string> DeniedNameCasings()
    {
        foreach (var name in ExpectedDeniedNames)
        {
            yield return name;
            yield return name.ToUpperInvariant();
            yield return name.ToLowerInvariant();
        }
    }

    [Test]
    public void All_ReturnsExactlyTheExpectedDeniedNames()
    {
        Assert.That(CredentialAttributes.All, Is.EquivalentTo(ExpectedDeniedNames));
    }

    [TestCaseSource(nameof(DeniedNameCasings))]
    public void IsCredentialAttribute_DeniedNameInAnyCasing_ReturnsTrue(string attributeName)
    {
        Assert.That(CredentialAttributes.IsCredentialAttribute(attributeName), Is.True,
            $"'{attributeName}' must be recognised as a credential attribute regardless of casing.");
    }

    [TestCase("UNICODEPWD")]
    [TestCase("unicodepwd")]
    [TestCase("UnicodePwd")]
    [TestCase("unicodePwd")]
    public void IsCredentialAttribute_UnicodePwdInAnyCasing_ReturnsTrue(string attributeName)
    {
        Assert.That(CredentialAttributes.IsCredentialAttribute(attributeName), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsCredentialAttribute_NullOrEmptyName_ReturnsFalse(string? attributeName)
    {
        Assert.That(CredentialAttributes.IsCredentialAttribute(attributeName), Is.False);
    }

    [TestCase("displayName")]
    [TestCase("sAMAccountName")]
    public void IsCredentialAttribute_NonCredentialAttribute_ReturnsFalse(string attributeName)
    {
        Assert.That(CredentialAttributes.IsCredentialAttribute(attributeName), Is.False);
    }

    [TestCase("displayName")]
    [TestCase("sAMAccountName")]
    public void HasCredentialLikeName_NonCredentialAttribute_ReturnsFalse(string attributeName)
    {
        Assert.That(CredentialAttributes.HasCredentialLikeName(attributeName), Is.False);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void HasCredentialLikeName_NullOrEmptyName_ReturnsFalse(string? attributeName)
    {
        Assert.That(CredentialAttributes.HasCredentialLikeName(attributeName), Is.False);
    }

    [TestCase("customPasswordField")]
    [TestCase("legacyPasswdStore")]
    [TestCase("corpPwdVault")]
    [TestCase("apiCredentialBlob")]
    [TestCase("clientSecret")]
    [TestCase("CUSTOMPASSWORDFIELD")]
    public void HasCredentialLikeName_CredentialLikeName_ReturnsTrue(string attributeName)
    {
        Assert.That(CredentialAttributes.HasCredentialLikeName(attributeName), Is.True);
    }

    [TestCaseSource(nameof(DeniedNameCasings))]
    public void HasCredentialLikeName_DeniedName_ReturnsFalse(string attributeName)
    {
        // A denylisted name is blocked outright, so it must never also be reported as a "warn but do not block"
        // candidate; that would show the administrator a warning about something they cannot select anyway.
        Assert.That(CredentialAttributes.HasCredentialLikeName(attributeName), Is.False,
            $"'{attributeName}' is on the denylist, so it is blocked rather than warned about.");
    }

    [TestCaseSource(nameof(KnownWarningFalsePositives))]
    public void HasCredentialLikeName_KnownFalsePositive_ReturnsTrue(string attributeName)
    {
        // Documented, accepted imprecision: these hold no credential material but do match the heuristic.
        Assert.That(CredentialAttributes.HasCredentialLikeName(attributeName), Is.True);
    }

    [TestCaseSource(nameof(KnownWarningFalsePositives))]
    public void IsCredentialAttribute_KnownWarningFalsePositive_ReturnsFalse(string attributeName)
    {
        // The whole point of the two-method split: a heuristic match must never block a legitimate attribute such
        // as pwdLastSet, which administrators routinely import.
        Assert.That(CredentialAttributes.IsCredentialAttribute(attributeName), Is.False,
            $"'{attributeName}' matches the warning heuristic but must remain selectable.");
    }

    [Test]
    public void All_EveryEntry_IsRecognisedByIsCredentialAttribute()
    {
        Assert.That(CredentialAttributes.All.All(CredentialAttributes.IsCredentialAttribute), Is.True);
    }
}
