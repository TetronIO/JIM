// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview.Patterns;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The curated pattern detectors (#827 Phase 4b).
///
/// A pattern is a claim about what a change means, so the bar for making one is high and the cost of staying silent
/// is low: an unlabelled group still carries its exact count and its values. Every detector is therefore written to
/// return null the moment it is not certain, and the negative cases below matter more than the positive ones.
/// </summary>
[TestFixture]
public class PreviewPatternDetectorTests
{
    private static PreviewPatternCandidate Candidate(string? oldValue, string? newValue, string? attributeName = "Email") =>
        new(attributeName, oldValue, newValue);

    #region Casing

    [Test]
    public void CasingChangeDetector_SameTextInDifferentCase_Detects()
    {
        var detector = new CasingChangeDetector();

        Assert.That(detector.Detect(Candidate("bob.smith@contoso.com", "Bob.Smith@Contoso.com")),
            Is.EqualTo(PreviewPatternKeys.CasingChanged));
    }

    [Test]
    public void CasingChangeDetector_DifferentText_IsSilent()
    {
        var detector = new CasingChangeDetector();

        Assert.That(detector.Detect(Candidate("bob@contoso.com", "bob@fabrikam.com")), Is.Null);
    }

    [Test]
    public void CasingChangeDetector_IdenticalValues_IsSilent()
    {
        var detector = new CasingChangeDetector();

        Assert.That(detector.Detect(Candidate("bob@contoso.com", "bob@contoso.com")), Is.Null,
            "no change at all is not a casing change");
    }

    [Test]
    public void CasingChangeDetector_ValueBecomingOrLeavingEmpty_IsSilent()
    {
        var detector = new CasingChangeDetector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detector.Detect(Candidate(null, "bob@contoso.com")), Is.Null);
            Assert.That(detector.Detect(Candidate("bob@contoso.com", "")), Is.Null);
        }
    }

    #endregion

    #region Email and UPN domain

    [Test]
    public void EmailDomainChangeDetector_SameLocalPartDifferentDomain_Detects()
    {
        var detector = new EmailDomainChangeDetector();

        Assert.That(detector.Detect(Candidate("bob.smith@contoso.com", "bob.smith@fabrikam.com")),
            Is.EqualTo(PreviewPatternKeys.EmailDomainChanged));
    }

    [Test]
    public void EmailDomainChangeDetector_UserPrincipalNameShape_Detects()
    {
        var detector = new EmailDomainChangeDetector();

        Assert.That(detector.Detect(Candidate("bsmith@contoso.local", "bsmith@contoso.com", attributeName: "UserPrincipalName")),
            Is.EqualTo(PreviewPatternKeys.EmailDomainChanged),
            "a UPN is the same shape as an address, and a domain cutover moves both");
    }

    [Test]
    public void EmailDomainChangeDetector_LocalPartAlsoChanged_IsSilent()
    {
        var detector = new EmailDomainChangeDetector();

        Assert.That(detector.Detect(Candidate("bob.smith@contoso.com", "robert.smith@fabrikam.com")), Is.Null,
            "calling this a domain change would hide that the identity part of the address moved too");
    }

    [Test]
    public void EmailDomainChangeDetector_SameDomain_IsSilent()
    {
        var detector = new EmailDomainChangeDetector();

        Assert.That(detector.Detect(Candidate("bob@contoso.com", "robert@contoso.com")), Is.Null);
    }

    [Test]
    public void EmailDomainChangeDetector_NotAddressShaped_IsSilent()
    {
        var detector = new EmailDomainChangeDetector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detector.Detect(Candidate("Sales", "Marketing")), Is.Null, "no at-sign at all");
            Assert.That(detector.Detect(Candidate("bob@@contoso.com", "bob@@fabrikam.com")), Is.Null, "two at-signs is not an address this detector will guess at");
            Assert.That(detector.Detect(Candidate("@contoso.com", "@fabrikam.com")), Is.Null, "an empty local part is a fragment, not an address");
            Assert.That(detector.Detect(Candidate("bob@", "robert@")), Is.Null, "an empty domain is a fragment, not an address");
        }
    }

    #endregion

    #region Container (distinguished name parent path)

    [Test]
    public void ContainerChangeDetector_SameLeafDifferentParent_Detects()
    {
        var detector = new ContainerChangeDetector();

        Assert.That(detector.Detect(Candidate(
                "CN=Bob Smith,OU=Sales,DC=contoso,DC=com",
                "CN=Bob Smith,OU=Marketing,DC=contoso,DC=com",
                attributeName: "DistinguishedName")),
            Is.EqualTo(PreviewPatternKeys.ContainerChanged));
    }

    [Test]
    public void ContainerChangeDetector_DifferentDepth_Detects()
    {
        var detector = new ContainerChangeDetector();

        Assert.That(detector.Detect(Candidate(
                "CN=Bob Smith,OU=Sales,DC=contoso,DC=com",
                "CN=Bob Smith,OU=North,OU=Sales,DC=contoso,DC=com",
                attributeName: "DistinguishedName")),
            Is.EqualTo(PreviewPatternKeys.ContainerChanged),
            "moving deeper into the tree is still a move");
    }

    [Test]
    public void ContainerChangeDetector_LeafRenamed_IsSilent()
    {
        var detector = new ContainerChangeDetector();

        Assert.That(detector.Detect(Candidate(
            "CN=Bob Smith,OU=Sales,DC=contoso,DC=com",
            "CN=Robert Smith,OU=Marketing,DC=contoso,DC=com",
            attributeName: "DistinguishedName")), Is.Null,
            "the object was renamed as well as moved; 'moved to a different container' would be a half-truth");
    }

    [Test]
    public void ContainerChangeDetector_SameParent_IsSilent()
    {
        var detector = new ContainerChangeDetector();

        Assert.That(detector.Detect(Candidate(
            "CN=Bob Smith,OU=Sales,DC=contoso,DC=com",
            "CN=Robert Smith,OU=Sales,DC=contoso,DC=com",
            attributeName: "DistinguishedName")), Is.Null);
    }

    [Test]
    public void ContainerChangeDetector_NotDistinguishedNameShaped_IsSilent()
    {
        var detector = new ContainerChangeDetector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detector.Detect(Candidate("Sales, Marketing and Support", "Sales, Marketing and Service")), Is.Null,
                "prose containing a comma is not a distinguished name");
            Assert.That(detector.Detect(Candidate("CN=Bob Smith", "CN=Bob Smith")), Is.Null, "a single relative name has no parent path");
            Assert.That(detector.Detect(Candidate("Sales", "Marketing")), Is.Null);
        }
    }

    #endregion

    #region Prefix and suffix

    [Test]
    public void AffixChangeDetector_TextAppended_DetectsSuffixAdded()
    {
        var detector = new AffixChangeDetector();

        Assert.That(detector.Detect(Candidate("bsmith", "bsmith_disabled")), Is.EqualTo(PreviewPatternKeys.SuffixAdded));
    }

    [Test]
    public void AffixChangeDetector_TextRemovedFromTheEnd_DetectsSuffixRemoved()
    {
        var detector = new AffixChangeDetector();

        Assert.That(detector.Detect(Candidate("bsmith_disabled", "bsmith")), Is.EqualTo(PreviewPatternKeys.SuffixRemoved));
    }

    [Test]
    public void AffixChangeDetector_TextPrepended_DetectsPrefixAdded()
    {
        var detector = new AffixChangeDetector();

        Assert.That(detector.Detect(Candidate("bsmith", "svc-bsmith")), Is.EqualTo(PreviewPatternKeys.PrefixAdded));
    }

    [Test]
    public void AffixChangeDetector_TextRemovedFromTheStart_DetectsPrefixRemoved()
    {
        var detector = new AffixChangeDetector();

        Assert.That(detector.Detect(Candidate("svc-bsmith", "bsmith")), Is.EqualTo(PreviewPatternKeys.PrefixRemoved));
    }

    [Test]
    public void AffixChangeDetector_ChangeReadableAsEitherEnd_IsSilent()
    {
        var detector = new AffixChangeDetector();

        Assert.That(detector.Detect(Candidate("ab", "abab")), Is.Null,
            "the same edit is a prefix addition and a suffix addition; naming one would be a coin toss");
    }

    [Test]
    public void AffixChangeDetector_UnrelatedValues_IsSilent()
    {
        var detector = new AffixChangeDetector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detector.Detect(Candidate("bsmith", "rjones")), Is.Null);
            Assert.That(detector.Detect(Candidate("bsmith", "bsmith")), Is.Null);
            Assert.That(detector.Detect(Candidate("", "bsmith")), Is.Null,
                "everything is an affix of an empty value; that is a value being set, not text being added to one");
            Assert.That(detector.Detect(Candidate("bsmith", null)), Is.Null);
        }
    }

    [Test]
    public void AffixChangeDetector_AffixDifferingOnlyInCase_IsSilent()
    {
        var detector = new AffixChangeDetector();

        Assert.That(detector.Detect(Candidate("bsmith", "BSMITH_disabled")), Is.Null,
            "the original text did not survive intact, so this is not simply an addition");
    }

    #endregion

    #region Registry

    [Test]
    public void Default_CasingWinsOverDomain_BecauseItIsTheNarrowerClaim()
    {
        Assert.That(PreviewPatternDetectorRegistry.Default.Detect(Candidate("bob@contoso.com", "bob@CONTOSO.com")),
            Is.EqualTo(PreviewPatternKeys.CasingChanged),
            "the domain is the same domain; reporting a domain change would send an administrator looking for a cutover that is not happening");
    }

    [Test]
    public void Default_DomainWinsOverSuffix_BecauseItIsTheMeaningfulReading()
    {
        Assert.That(PreviewPatternDetectorRegistry.Default.Detect(Candidate("bob@contoso.com", "bob@contoso.com.au")),
            Is.EqualTo(PreviewPatternKeys.EmailDomainChanged),
            "'suffix added' is true and useless next to 'the domain moved'");
    }

    [Test]
    public void Default_NothingRecognisable_IsSilent()
    {
        Assert.That(PreviewPatternDetectorRegistry.Default.Detect(Candidate("2026-09-01", "2026-10-15")), Is.Null,
            "a preview whose deltas are dates has no pattern to name, and inventing one is worse than none");
    }

    [Test]
    public void Default_ReadTwice_GivesTheSameAnswer()
    {
        var first = PreviewPatternDetectorRegistry.Default.Detect(Candidate("bsmith", "svc-bsmith"));
        var second = PreviewPatternDetectorRegistry.Default.Detect(Candidate("bsmith", "svc-bsmith"));

        Assert.That(second, Is.EqualTo(first), "detection feeds a persisted summary; the same preview re-run must read the same");
    }

    [Test]
    public void Detect_ADetectorThatThrows_IsSkippedRatherThanFailingThePreview()
    {
        var registry = new PreviewPatternDetectorRegistry([new ThrowingDetector(), new CasingChangeDetector()]);

        Assert.That(registry.Detect(Candidate("bob", "BOB")), Is.EqualTo(PreviewPatternKeys.CasingChanged),
            "a pattern label is decoration; a bug in one detector must not take down an otherwise correct preview of 40,000 objects");
    }

    [Test]
    public void Constructor_NoDetectors_IsRejected()
    {
        Assert.That(() => new PreviewPatternDetectorRegistry([]), Throws.TypeOf<ArgumentException>(),
            "an empty registry silently labels nothing, which is indistinguishable from detectors that all declined");
    }

    private sealed class ThrowingDetector : IPreviewPatternDetector
    {
        public string? Detect(PreviewPatternCandidate candidate) => throw new InvalidOperationException("deliberate");
    }

    #endregion
}
