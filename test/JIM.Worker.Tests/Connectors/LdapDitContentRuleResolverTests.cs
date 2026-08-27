// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A DIT Content Rule states which auxiliary classes a directory permits on entries of one structural class, and is
/// the only place in an RFC 4512 schema that says so. It names those classes however the directory felt like it:
/// by descriptor, by OID, and in whatever case. Turning that into something JIM can offer an administrator means
/// resolving every reference back to a class the schema actually publishes.
/// </summary>
/// <remarks>
/// These are suggestions, never configuration. What an administrator ends up managing is their own selection; a rule
/// only narrows the list JIM offers them.
/// </remarks>
[TestFixture]
public class LdapDitContentRuleResolverTests
{
    private static Rfc4512ObjectClassIndex BuildIndex()
    {
        return Rfc4512SchemaParser.IndexObjectClasses(
        [
            "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST ( sn $ cn ) )",
            "( 1.3.6.1.1.1.2.0 NAME 'posixAccount' SUP top AUXILIARY MUST ( cn $ uid $ uidNumber ) )",
            "( 1.3.6.1.1.1.2.1 NAME 'shadowAccount' SUP top AUXILIARY MUST uid )",
            "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' SUP person STRUCTURAL MAY mail )"
        ]);
    }

    private static Rfc4512DitContentRuleDescription Rule(string definition)
    {
        var rule = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);
        Assert.That(rule, Is.Not.Null, "the test's own rule string must parse");
        return rule!;
    }

    [Test]
    public void Resolve_WithAuxClassesNamedByDescriptor_ReturnsThoseClasses()
    {
        var result = LdapDitContentRuleResolver.ResolvePermittedAuxiliaryClasses(
            Rule("( 2.5.6.6 AUX ( posixAccount $ shadowAccount ) )"), BuildIndex());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AuxiliaryClassNames, Is.EquivalentTo(new[] { "posixAccount", "shadowAccount" }));
            Assert.That(result.UnresolvedReferences, Is.Empty);
        }
    }

    [Test]
    public void Resolve_WithAuxClassesNamedByOid_ReturnsTheClassNames()
    {
        // A rule may reference classes by OID. An Object Type is named, so an unresolved OID would be useless to
        // every surface downstream.
        var result = LdapDitContentRuleResolver.ResolvePermittedAuxiliaryClasses(
            Rule("( 2.5.6.6 AUX ( 1.3.6.1.1.1.2.0 $ 1.3.6.1.1.1.2.1 ) )"), BuildIndex());

        Assert.That(result.AuxiliaryClassNames, Is.EquivalentTo(new[] { "posixAccount", "shadowAccount" }));
    }

    [Test]
    public void Resolve_WithAnAuxClassSpeltInADifferentCase_ReturnsTheSchemasOwnSpelling()
    {
        // LDAP descriptors are case-insensitive, so a rule and a class definition may disagree on case. The
        // schema's spelling is the one that will match an Object Type's name.
        var result = LdapDitContentRuleResolver.ResolvePermittedAuxiliaryClasses(
            Rule("( 2.5.6.6 AUX POSIXACCOUNT )"), BuildIndex());

        Assert.That(result.AuxiliaryClassNames, Is.EquivalentTo(new[] { "posixAccount" }));
    }

    [Test]
    public void Resolve_WithAReferenceTheSchemaDoesNotPublish_ReportsItUnresolvedRatherThanDroppingIt()
    {
        // Suggesting a class that does not exist would offer an administrator something they can never select, but
        // saying nothing at all would hide a schema the directory is serving inconsistently. Report it instead.
        var result = LdapDitContentRuleResolver.ResolvePermittedAuxiliaryClasses(
            Rule("( 2.5.6.6 AUX ( posixAccount $ noSuchClass ) )"), BuildIndex());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AuxiliaryClassNames, Is.EquivalentTo(new[] { "posixAccount" }));
            Assert.That(result.UnresolvedReferences, Is.EquivalentTo(new[] { "noSuchClass" }));
        }
    }

    [Test]
    public void Resolve_WithAReferenceToAClassThatIsNotAuxiliary_ReportsItUnresolved()
    {
        // An AUX list naming a structural class is a directory serving a schema that contradicts itself. Treating
        // it as auxiliary would let JIM merge a structural class's attributes into another structural type.
        var result = LdapDitContentRuleResolver.ResolvePermittedAuxiliaryClasses(
            Rule("( 2.5.6.6 AUX ( posixAccount $ inetOrgPerson ) )"), BuildIndex());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AuxiliaryClassNames, Is.EquivalentTo(new[] { "posixAccount" }));
            Assert.That(result.UnresolvedReferences, Is.EquivalentTo(new[] { "inetOrgPerson" }));
        }
    }

    [Test]
    public void Resolve_WithTheSameClassNamedTwice_ReturnsItOnce()
    {
        // Tags are unique per key and value, so a duplicate would be rejected by the database rather than ignored.
        var result = LdapDitContentRuleResolver.ResolvePermittedAuxiliaryClasses(
            Rule("( 2.5.6.6 AUX ( posixAccount $ 1.3.6.1.1.1.2.0 ) )"), BuildIndex());

        Assert.That(result.AuxiliaryClassNames, Is.EquivalentTo(new[] { "posixAccount" }));
    }

    [Test]
    public void Resolve_WithARuleThatPermitsNoAuxClasses_ReturnsNothing()
    {
        var result = LdapDitContentRuleResolver.ResolvePermittedAuxiliaryClasses(
            Rule("( 2.5.6.6 NAME 'personContentRule' MUST uid )"), BuildIndex());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AuxiliaryClassNames, Is.Empty);
            Assert.That(result.UnresolvedReferences, Is.Empty);
        }
    }
}
