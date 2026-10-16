---
title: LDAP Connector Operational Attributes
description: "Import the attributes an RFC 4512 directory maintains itself, such as createTimestamp and pwdFailureTime."
type: object
audience: administrator
---

# LDAP Connector Operational Attributes

An RFC 4512 directory keeps some of the most useful facts about an entry in operational attributes, which the directory maintains itself rather than holding as data you write: `createTimestamp`, `modifyTimestamp`, `creatorsName` and `memberOf`, and with OpenLDAP's password policy overlay `pwdChangedTime`, `pwdAccountLockedTime` and `pwdFailureTime`. An operational attribute belongs to no object class, and a directory returns one only when it is asked for by name.

Schema discovery adds each operational attribute the directory publishes to every structural Object Type. Each one is:

- **Unselected**<br /> Nothing is imported until you select it, so an operational attribute costs nothing until then.
- **Read-only**<br /> JIM can import it but never export it, so it cannot be the target of an Attribute Flow.
- **Marked operational**<br /> The Schema tab holds back the operational attributes you have not selected, says how many it is holding back, and offers **Show operational attributes** to list them. Their **Class** column reads **Operational**, because they belong to no class. An operational attribute you have selected is always listed. See [Operational attributes on the Schema tab](../configuration/connected-system-schema.md#operational-attributes-on-the-schema-tab).

Some operational attributes are never offered, because no entry JIM imports can hold a value for them: those only the subschema subentry or the root DSE carries (`attributeTypes`, `namingContexts`, `supportedControl` and the like), OpenLDAP's access control pseudo-attributes (`entry`, `children`), and any the directory declares obsolete. Password history (`pwdHistory` on OpenLDAP, `passwordHistory` on 389 Directory Server) holds hashes of previous passwords, so it is never offered either; see [Credential attributes are never managed](../configuration/connected-system-attributes.md#credential-attributes-are-never-managed).

To import an operational attribute, select it as you would any other: choose **Show operational attributes** and switch it on, or name it in **Select attributes via CSV**. In PowerShell, find it with `Get-JIMConnectedSystemObjectType` (each attribute carries `isOperational`) and select it with `Set-JIMConnectedSystemAttribute -Selected $true`. The REST API returns `isOperational` on every attribute, and selects one through the same attribute update as any other.

Two things decide whether a value arrives. The account JIM connects as needs read access to the attribute: the rules in [Service Account Permissions](jim-ldap-connector-permissions.md) grant read on whole entries, operational attributes included, but a rule of your own that lists attributes (`attrs=`) must list the operational ones you select. And the directory must be maintaining the value: OpenLDAP's password policy overlay records `pwdFailureTime` only on an entry a password policy applies to, through the overlay's default policy (`olcPPolicyDefault`) or the entry's own `pwdPolicySubentry`. `pwdFailureTime` holds one timestamp per consecutive failed bind, so it is multi-valued; see [How values are read](jim-ldap-connector.md#how-values-are-read) for what JIM does with more than one date.

A Connected System whose schema was retrieved before JIM offered operational attributes gains them at its next schema refresh. The refresh preview lists them as added attributes, unselected, and changes nothing you have already selected.

Active Directory and Samba AD are unaffected. Their discovery reads the schema partition, where the `top` class already lists the operational attributes an administrator uses (`whenCreated`, `whenChanged`, `uSNChanged`, `memberOf`), so those are discovered as ordinary attributes and are not marked operational.
