// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Text.RegularExpressions;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Tells a directory's "this value is already in use" rejection apart from every other export failure, and names the
/// attribute where the directory named it (Unique Value Generation, #242, release 4, decision 9).
/// <para>
/// Classification is a table of (server family, result code, message pattern) read in order, first match wins.
/// Neither the result code nor the extended error code is enough alone: entryAlreadyExists (68) is how Active
/// Directory reports an account name in use, an object already at the DN, and (with the same 00002071 code it uses
/// for the DN) Samba AD reports an account name in use; constraintViolation (19) also covers schema and password
/// policy refusals, which are not collisions at all. So the message decides, and a constraint violation with no
/// uniqueness wording is never classified.
/// </para>
/// <para>
/// Every message pattern is matched whichever server family the Connector detected. The patterns are specific to the
/// server that writes them and cannot collide, and reading them regardless means a misdetected server is still
/// attributed correctly rather than falling to the generic entryAlreadyExists rule at the end of the table.
/// </para>
/// <para>
/// Attribution never guesses: an attribute is returned only when the server's own text names it, or when the code
/// itself identifies it (Active Directory's ERROR_USER_EXISTS is the account name). Otherwise the rejection is
/// classified with no attribute, and Collision Remediation attributes it some other way or not at all.
/// </para>
/// </summary>
internal static class LdapUniquenessRejectionClassifier
{
    /// <summary>The attribute name JIM's LDAP Connector uses for an object's DN.</summary>
    private const string DistinguishedNameAttribute = "distinguishedName";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    // Samba's samldb module names the attribute and quotes the value: "samldb: sAMAccountName 'x' already in use!"
    // and "samldb: userPrincipalName 'x' is already in use ".
    private static readonly Regex SambaAttributeInUse = Pattern(@"samldb: (?<attribute>[A-Za-z][\w-]*) '[^']*' (?:is )?already in use");

    // Active Directory's "Att <hex ATTRTYP> (<lDAPDisplayName>)" clause on an attribute error.
    private static readonly Regex ActiveDirectoryAttClause = Pattern(@"\bAtt [0-9A-F]+ \((?<attribute>[A-Za-z][\w-]*)\)");

    // slapo-unique: "non-unique attributes found with (|(mail=x))", one filter component per value checked.
    private static readonly Regex OpenLdapFilterComponent = Pattern(@"\((?<attribute>[A-Za-z][\w;.-]*)=");

    /// <summary>
    /// The classification table, in the order it is read. Each row names the server family whose message it reads,
    /// the result code it applies to and what it attributes.
    /// </summary>
    internal static IReadOnlyList<LdapUniquenessRejectionRule> Rules { get; } =
    [
        new("Samba AD", ResultCode.EntryAlreadyExists, SambaAttributeInUse, FromGroup,
            "samldb names the attribute in use (sAMAccountName, under Active Directory's 00002071 code)"),
        new("Samba AD", ResultCode.ConstraintViolation, SambaAttributeInUse, FromGroup,
            "samldb names the attribute in use (userPrincipalName)"),
        new("Samba AD", ResultCode.ConstraintViolation, Pattern(@"samldb: spn\[[^\]]*\] would cause a conflict"), (_, _) => "servicePrincipalName",
            "samldb reports a service principal name already held"),
        new("Active Directory", ResultCode.EntryAlreadyExists, Pattern(@"\b00000524:"), (_, _) => "sAMAccountName",
            "ERROR_USER_EXISTS: the account name is in use"),
        new("Active Directory", ResultCode.ConstraintViolation, Pattern(@"\b000021C8:"), (_, message) => AttClause(message, "userPrincipalName"),
            "ERROR_DS_UPN_VALUE_NOT_UNIQUE_IN_FOREST: the attribute named in the Att clause, the UPN by definition"),
        new("Active Directory", ResultCode.ConstraintViolation, Pattern(@"\b000021C7:"), (_, message) => AttClause(message, "servicePrincipalName"),
            "ERROR_DS_SPN_VALUE_NOT_UNIQUE_IN_FOREST: the attribute named in the Att clause, the SPN by definition"),
        new("Active Directory", ResultCode.EntryAlreadyExists, Pattern(@"\b00002071:"), (_, _) => DistinguishedNameAttribute,
            "ERROR_DS_OBJ_STRING_NAME_EXISTS: an object already exists at the DN"),
        new("OpenLDAP (slapo-unique)", ResultCode.ConstraintViolation, Pattern(@"non-unique attributes found with (?<filter>.*)"), SingleFilterAttribute,
            "the overlay's search filter names the attribute; unattributed when it names more than one"),
        new("OpenLDAP (slapo-unique)", ResultCode.ConstraintViolation, Pattern(@"some attributes not unique"), (_, _) => null,
            "the overlay's older wording, which names no attribute"),
        new("389 Directory Server (Attribute Uniqueness)", ResultCode.ConstraintViolation,
            Pattern(@"Another entry with the same attribute value already exists \(attribute: ""(?<attribute>[^""]*)""\)"), FromGroup,
            "the plug-in names the attribute"),
        new("Samba AD", ResultCode.EntryAlreadyExists, Pattern(@"^Entry .+ already exists$"), (_, _) => DistinguishedNameAttribute,
            "an entry already exists at the DN"),
        new("Any LDAP directory", ResultCode.EntryAlreadyExists, null, (_, _) => DistinguishedNameAttribute,
            "entryAlreadyExists with nothing more specific: an entry already exists at the DN (an add, or a rename onto one)")
    ];

    /// <summary>
    /// Classifies a directory rejection from its result code and the server's diagnostic message.
    /// </summary>
    /// <param name="resultCode">The LDAP result code the directory returned.</param>
    /// <param name="serverMessage">The directory's diagnostic message, if any.</param>
    /// <param name="rejectedAttributeName">The attribute the directory named as holding a value already in use, or
    /// null when it named none or the rejection is not a uniqueness rejection.</param>
    /// <returns>True when the rejection means a value is already in use.</returns>
    internal static bool TryClassify(ResultCode resultCode, string? serverMessage, out string? rejectedAttributeName)
    {
        var message = serverMessage?.Trim() ?? string.Empty;
        foreach (var rule in Rules.Where(r => r.ResultCode == resultCode))
        {
            if (rule.MessagePattern == null)
            {
                rejectedAttributeName = rule.Attribute(null, message);
                return true;
            }

            var match = rule.MessagePattern.Match(message);
            if (!match.Success)
                continue;

            rejectedAttributeName = rule.Attribute(match, message);
            return true;
        }

        rejectedAttributeName = null;
        return false;
    }

    /// <summary>
    /// Classifies the exception an export failed with. A rejection arrives either as the library's
    /// <see cref="DirectoryOperationException"/> carrying the response, or as an <see cref="LdapException"/> the
    /// Connector raised itself from a non-success response, whose error code is the LDAP result code.
    /// </summary>
    internal static bool TryClassify(Exception exception, out string? rejectedAttributeName)
    {
        switch (exception)
        {
            case DirectoryOperationException { Response: { } response }:
                return TryClassify(response.ResultCode, response.ErrorMessage, out rejectedAttributeName);
            case LdapException ldapException:
                return TryClassify((ResultCode)ldapException.ErrorCode, ldapException.ServerErrorMessage ?? ldapException.Message, out rejectedAttributeName);
            default:
                rejectedAttributeName = null;
                return false;
        }
    }

    private static string? FromGroup(Match? match, string message)
    {
        var name = match?.Groups["attribute"].Value.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string AttClause(string message, string byDefinition)
    {
        var clause = ActiveDirectoryAttClause.Match(message);
        return clause.Success ? clause.Groups["attribute"].Value : byDefinition;
    }

    private static string? SingleFilterAttribute(Match? match, string message)
    {
        if (match == null)
            return null;

        var attributes = OpenLdapFilterComponent.Matches(match.Groups["filter"].Value)
            .Select(component => component.Groups["attribute"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return attributes.Count == 1 ? attributes[0] : null;
    }
}
