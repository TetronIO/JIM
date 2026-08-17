// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// Turns a DIT Content Rule's AUX references into the auxiliary class names an administrator could actually select.
/// </summary>
/// <remarks>
/// A rule may name a class by descriptor or by OID, and in any case, so a reference has to be resolved against the
/// schema before it means anything to JIM: an Object Type is identified by name. References that resolve to nothing,
/// or to a class that is not auxiliary, are reported rather than dropped, because a directory serving a schema that
/// contradicts itself is something an administrator should hear about rather than silently get fewer suggestions
/// from.
/// </remarks>
internal static class LdapDitContentRuleResolver
{
    internal static DitContentRuleResolution ResolvePermittedAuxiliaryClasses(
        Rfc4512DitContentRuleDescription rule,
        Rfc4512ObjectClassIndex classIndex)
    {
        var resolution = new DitContentRuleResolution();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in rule.AuxiliaryClasses)
        {
            // A reference is either a descriptor or an OID; try both rather than guessing from its shape, because
            // RFC 4512 descriptors are not required to be non-numeric and a directory may spell either way.
            if (!classIndex.ByName.TryGetValue(reference, out var referenced))
                classIndex.ByOid.TryGetValue(reference, out referenced);

            if (referenced?.Name == null || referenced.Kind != Rfc4512ObjectClassKind.Auxiliary)
            {
                resolution.UnresolvedReferences.Add(reference);
                continue;
            }

            // The schema's own spelling, not the rule's: that is what the Object Type will be named.
            if (seen.Add(referenced.Name))
                resolution.AuxiliaryClassNames.Add(referenced.Name);
        }

        return resolution;
    }
}

/// <summary>
/// What a DIT Content Rule's AUX list resolved to against the directory's published classes.
/// </summary>
internal class DitContentRuleResolution
{
    /// <summary>
    /// The auxiliary classes the rule permits, named as the schema names them.
    /// </summary>
    public List<string> AuxiliaryClassNames { get; } = [];

    /// <summary>
    /// References that named no auxiliary class the schema publishes. Carried so discovery can say so rather than
    /// quietly offering an administrator a shorter list.
    /// </summary>
    public List<string> UnresolvedReferences { get; } = [];
}
