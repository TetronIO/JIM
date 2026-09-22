// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Text;
namespace JIM.Connectors.LDAP;

/// <summary>
/// What a change log kept of an entry that has since been deleted, read back out of the LDIF-shaped lines it
/// stores: the object classes, which resolve the Object Type the deletion is recorded against, and the entryUUID,
/// which is the external id the deletion is matched on. OpenLDAP's accesslog keeps these lines in <c>reqOld</c>
/// (one attribute value per line) and 389 Directory Server's Retro Changelog in the delete record's <c>changes</c>
/// attribute (one LDIF text, once <c>nsslapd-log-deleted</c> is on); both are read here so that a deletion from
/// either builds the same import object a live entry would have, which is the only kind the import can match.
/// <para>
/// The lines are LDIF (RFC 2849) in all but the <c>dn:</c> line, which neither log keeps: a line beginning with a
/// single space continues the one before it, <c>name: value</c> is plain, <c>name:: value</c> is base64, and
/// <c>name:&lt; url</c> points elsewhere and carries nothing usable. Names compare case-insensitively, as LDAP's do.
/// </para>
/// </summary>
internal sealed class LdapDeletedEntryIdentity
{
    private const string EntryUuidAttributeName = "entryUUID";

    /// <summary>The deleted entry's object classes, in the order the log listed them; empty when it listed none.</summary>
    internal IReadOnlyList<string> ObjectClasses { get; }

    /// <summary>The deleted entry's entryUUID, or null when neither the log nor the caller supplied one.</summary>
    internal string? EntryUuid { get; }

    /// <summary>
    /// Whether the lines described no entry at all: no object class and no entryUUID. Every entry has an object
    /// class, so this is how a log says it did not record the deleted entry, as distinct from recording one that
    /// this Connected System does not import.
    /// </summary>
    internal bool IsEmpty => ObjectClasses.Count == 0 && EntryUuid == null;

    private LdapDeletedEntryIdentity(IReadOnlyList<string> objectClasses, string? entryUuid)
    {
        ObjectClasses = objectClasses;
        EntryUuid = entryUuid;
    }

    /// <summary>
    /// Reads the object classes and entryUUID out of the lines.
    /// </summary>
    /// <param name="ldifLines">The LDIF-shaped lines describing the deleted entry, without a <c>dn:</c> line.</param>
    /// <param name="entryUuid">The entryUUID when the log states it directly (the accesslog's <c>reqEntryUUID</c>); it outranks any the lines carry.</param>
    internal static LdapDeletedEntryIdentity Parse(IEnumerable<string> ldifLines, string? entryUuid = null)
    {
        var objectClasses = new List<string>();
        string? parsedEntryUuid = null;

        foreach (var (name, value) in Attributes(Unfold(ldifLines)))
        {
            if (name.Equals(LdapConnectorConstants.ObjectClassAttributeName, StringComparison.OrdinalIgnoreCase))
                objectClasses.Add(value);
            else if (name.Equals(EntryUuidAttributeName, StringComparison.OrdinalIgnoreCase))
                parsedEntryUuid ??= value;
        }

        return new LdapDeletedEntryIdentity(objectClasses, string.IsNullOrEmpty(entryUuid) ? parsedEntryUuid : entryUuid);
    }

    /// <summary>
    /// Reads the lines and builds the import object in one step. Null, with a warning, when the deletion cannot
    /// be recorded against an Object Type or matched by external id.
    /// </summary>
    internal static ConnectedSystemImportObject? Identify(IEnumerable<string> ldifLines, string? deletedDn, string? entryUuid, IReadOnlyList<ConnectedSystemObjectType> objectTypes, ILogger logger) =>
        Parse(ldifLines, entryUuid).ToImportObject(deletedDn, objectTypes, logger);

    /// <summary>
    /// The deletion as an import object: the Object Type the classes resolve to (the same precedence a live entry
    /// gets, so the deletion is recorded against the type the object was imported as), the external id attribute
    /// carrying the entryUUID, and <c>distinguishedName</c> carrying the DN. Null, with a warning, when no selected
    /// Object Type matches or there is no entryUUID, since the import could match neither.
    /// </summary>
    internal ConnectedSystemImportObject? ToImportObject(string? deletedDn, IReadOnlyList<ConnectedSystemObjectType> objectTypes, ILogger logger)
    {
        var objectType = LdapObjectTypeMatcher.Match(ObjectClasses, objectTypes);
        if (objectType == null)
        {
            logger.Warning("LdapDeletedEntryIdentity: Could not determine object type for deleted object. DN: {Dn}. " +
                "The log's record of the deleted entry carries no objectClass naming a selected Object Type.", LogSanitiser.Sanitise(deletedDn));
            return null;
        }

        if (string.IsNullOrEmpty(EntryUuid))
        {
            logger.Warning("LdapDeletedEntryIdentity: Could not determine entryUUID for deleted object. DN: {Dn}. " +
                "The log's record of the deleted entry carries no entryUUID.", LogSanitiser.Sanitise(deletedDn));
            return null;
        }

        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = objectType.Name,
            ChangeType = ObjectChangeType.Deleted,
        };

        // The external id is what the import matches the deletion to an existing Connected System Object by.
        var externalIdAttribute = objectType.Attributes.FirstOrDefault(a => a.IsExternalId);
        if (externalIdAttribute != null)
        {
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
            {
                Name = externalIdAttribute.Name,
                StringValues = [EntryUuid]
            });
        }

        // The DN is the secondary external id.
        if (!string.IsNullOrEmpty(deletedDn))
        {
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
            {
                Name = "distinguishedName",
                StringValues = [deletedDn]
            });
        }

        logger.Debug("LdapDeletedEntryIdentity: Built delete import for {ObjectType} with entryUUID {Uuid}, DN: {Dn}",
            objectType.Name, LogSanitiser.Sanitise(EntryUuid), LogSanitiser.Sanitise(deletedDn));
        return importObject;
    }

    /// <summary>
    /// Joins continuation lines onto the line they continue (RFC 2849: a line beginning with a single space is the
    /// rest of the previous one), and drops the trailing carriage return a CRLF text leaves on each line.
    /// </summary>
    private static IEnumerable<string> Unfold(IEnumerable<string> lines)
    {
        StringBuilder? current = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length > 0 && line[0] == ' ' && current != null)
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }

            if (current != null)
                yield return current.ToString();

            current = new StringBuilder(line);
        }

        if (current != null)
            yield return current.ToString();
    }

    /// <summary>
    /// The attribute name and value of each unfolded line that carries one. Comments, blank lines, lines with no
    /// separator, URL-valued lines and undecodable base64 are passed over: none of them can name a class or an id.
    /// </summary>
    private static IEnumerable<(string Name, string Value)> Attributes(IEnumerable<string> unfoldedLines)
    {
        foreach (var line in unfoldedLines)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var name = line[..separator].Trim();
            var rest = line[(separator + 1)..];

            if (rest.StartsWith('<'))
                continue;

            if (rest.StartsWith(':'))
            {
                string decoded;
                try
                {
                    decoded = Encoding.UTF8.GetString(Convert.FromBase64String(rest[1..].Trim()));
                }
                catch (FormatException)
                {
                    continue;
                }

                yield return (name, decoded.Trim());
                continue;
            }

            yield return (name, rest.Trim());
        }
    }
}
