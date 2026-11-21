---
title: JIM Microsoft Entra ID Connector
description: Synchronise users and groups with a Microsoft Entra ID tenant through Microsoft Graph, and the settings the Connector takes.
type: object
audience: administrator
---

# JIM Microsoft Entra ID Connector

The JIM Microsoft Entra ID Connector synchronises users and groups with a Microsoft Entra ID tenant. It reaches the tenant through Microsoft Graph, signing in as an app registration in that tenant, with no agent or other software to install.

It imports users and groups, with their managers, members and owners, and exports changes to them. It works out where each object is managed, so export Synchronisation Rules can leave objects that Active Directory owns alone. With the right permission, it also takes JIM's passwords: Initial Password, Set Password and Password Synchronisation.

```jim-diagram
archetype: flow
title: How the Connector reaches a tenant
desc: "The Connected System holds the tenant, the app registration's client ID and a credential. The Connector uses the credential to get an access token from Microsoft Entra ID, for the app registration, then reads and writes the tenant's users and groups through Microsoft Graph with that token, as the app registration's permissions allow."
in: { label: Connected System, note: in JIM }
out: { label: Users and groups, note: in the tenant }
steps:
  - { id: token, label: Access token, note: from Entra ID, next: "permissions as granted" }
  - { id: graph, label: Microsoft Graph, note: "`$batch`, paced" }
```

## :material-rocket-launch-outline: Setting it up

The Connector needs an app registration in the tenant, with Microsoft Graph application permissions granted with admin consent, and a credential.

**With PowerShell (recommended).** [`Register-JIMEntraIdConnector`](../powershell/entra-id-connector.md) creates the app registration with least-privilege permissions, grants admin consent, makes a certificate whose private key only JIM holds, and creates the Connected System with everything set. Run it again later to check and repair the setup.

**By hand.** In the Microsoft Entra admin centre:

1. Under **App registrations**, select **New registration**. Name it, and choose **Accounts in this organizational directory only** <!-- conformance: allow LANG-2 "Microsoft's own label, quoted exactly" -->. No redirect URI is needed.
2. Under **API permissions**, add the **Microsoft Graph** **Application permissions** in [Permissions](#permissions) below, then select **Grant admin consent**. Delegated permissions do not work: the Connector signs in as the application, with no person present.
3. Under **Certificates & secrets**, add a credential:
    - **A certificate (recommended).** Upload the public part of a certificate with an RSA key of at least 2,048 bits. Give JIM the PFX, private key included, in the **Certificate** setting.
    - **A federated credential**, where JIM runs on an Azure virtual machine with a user-assigned managed identity attached. Add a federated credential for **Managed identity**, choosing that identity.
    - **A client secret**, for a quick trial only: it is sent with every token request, and expires.
4. From the app registration's **Overview**, copy the **Directory (tenant) ID** and **Application (client) ID**.

Then, in JIM, create a Connected System with this Connector, and give it the tenant, the client ID and the credential. Saving the settings tests the connection, and names any permission the app registration is missing.

### Permissions

| Permission | What the Connector uses it for |
|------------|--------------------------------|
| `User.ReadWrite.All` | Reading and writing users. `User.Read.All` is enough to import only. |
| `Group.ReadWrite.All` | Reading and writing groups and their members and owners. `Group.Read.All` is enough to import only. |
| `User-OnPremisesSyncBehavior.Read.All`, `Group-OnPremisesSyncBehavior.Read.All` | Telling which objects synchronised from Active Directory have moved to cloud management, for `sourceOfAuthority`. |
| `Member.Read.Hidden` | Reading the members of groups whose membership is hidden. See [Hidden membership](#hidden-membership). |
| `Directory.Read.All` | Optional: offering the tenant's directory extensions in the schema. |
| `User-PasswordProfile.ReadWrite.All` | Optional: setting passwords. See [Passwords](#passwords). |

Only import and export need the first two; the Connector warns about the others when it tests the connection, and carries on without them. Without `Directory.Read.All` the schema is the Connector's own list of user and group attributes, without the tenant's directory extensions.

## :material-star-four-points-outline: Features

### Users, groups and their relationships

The schema offers the users' and groups' identity attributes from Microsoft Graph, Exchange's `extensionAttribute1` to `extensionAttribute15`, and any directory extensions. Each object's External ID is its Entra ID `id`, which never changes.

A user's `manager`, and a group's `members` and `owners`, are references. Members that are users or groups are imported; devices, contacts and service principals are left out, and since the Connector adds and removes members one at a time, leaving them out never removes them. A group's membership is read in full on every import that reads it, so a group is never staged with only part of its members.

### Source of Authority

Every object carries `sourceOfAuthority`, which the Connector works out:

| Value | Where the object is managed |
|-------|-----------------------------|
| `cloud` | In Entra ID itself, so JIM can write to it. Includes an object synchronised from Active Directory that has since moved to cloud management. |
| `onPremisesDirectory` | In Active Directory, synchronised to Entra ID. Entra ID refuses changes to most of its attributes; write them in Active Directory instead. |
| `externalEntraDirectory` | A guest from another tenant. |

Scope export Synchronisation Rules on `sourceOfAuthority` so they write only to the objects JIM may change. An export refused because Active Directory owns the attribute says so, and names `sourceOfAuthority`.

### Full and Delta Import

A Full Import reads every user and group, and records where the next Delta Import starts from. A Delta Import asks Microsoft Graph what changed since then, reads each changed object again in full, and stages a removed object as deleted.

A Delta Import reads every object instead, and says why on its Activity, when:

- there is nothing to start from, as before the first Full Import, or for an Object Type selected since;
- Microsoft Graph no longer accepts where it was to start from. That happens after **seven days** without a Delta Import, so run one at least weekly;
- the selected attributes have changed since the last import, since Microsoft Graph reports changes only to what was read then;
- objects have moved to or from cloud management, which Microsoft Graph's changes do not report.

### Hidden membership

A Microsoft 365 group can hide its membership. Reading it needs `Member.Read.Hidden`. Without that permission, such a group's members are reported as unreadable rather than read as empty, and JIM keeps the members it already holds, so it never removes members it could not see. With it, anyone who can view the group in JIM sees its members.

### Export

Changes are written through Microsoft Graph's `$batch`, twenty requests at a time, paced to the **Maximum Writes Per Second** setting. Microsoft Graph allows each application 20 writes a second in a tenant. The Connector slows down further by itself while Microsoft Graph reports the tenant is near its limit, and waits as long as Graph asks when it throttles.

A new user is created with a long random password, which Microsoft Graph requires and nobody knows; it is never stored. Give users a password they can use with [Initial Password](../concepts/passwords.md), or sign them in another way, such as a Temporary Access Pass.

A deletion deletes the object, which Entra ID keeps for 30 days in case it needs restoring. With **Delete Behaviour** set to **Disable**, a user is disabled instead. A group cannot be disabled, so it is deleted either way.

### Passwords

The Connector takes passwords from JIM: [Initial Password](../concepts/passwords.md), Set Password and [Password Synchronisation](../concepts/password-synchronisation.md). Microsoft Graph needs two things for that: the `User-PasswordProfile.ReadWrite.All` permission, and the app registration holding the **User Administrator** directory role. Assign the role over an Administrative Unit holding the users JIM manages, not across the tenant: within its scope it can manage users and groups and reset passwords. `Register-JIMEntraIdConnector -PasswordManagement` does both.

Every expiry choice JIM offers is applied as asked: a change required at the next sign-in, expiry by the tenant's policy, or a password that never expires. Some passwords cannot be set here, and the delivery stops with Entra ID's reason rather than retrying:

- An administrator's password, which needs a more privileged role than JIM should hold.
- A user synchronised from Active Directory, whose password lives there; set it through the [JIM LDAP Connector](jim-ldap-connector.md).
- A user who signs in through another identity provider.

**Check password channel**, on the Connected System's Passwords tab, confirms the permission. A User Administrator role assigned over an Administrative Unit does not appear in what JIM can see, so the check says it cannot confirm the role rather than that it is missing.

### National clouds

The **Cloud** setting reaches a tenant in the US Government L4 (GCC High), US Government L5 (DoD) or China (operated by 21Vianet) cloud. A tenant is found only in its own cloud.

## :material-lan-connect: Connection Settings

### Microsoft Entra ID Tenant

| Setting | Required | Description |
|---------|----------|-------------|
| Tenant ID | Yes | The tenant's ID, a GUID, or one of its verified domain names, such as `contoso.onmicrosoft.com`. |
| Cloud | No | The cloud the tenant is in. Default `Global`. |

### Authentication

| Setting | Required | Description |
|---------|----------|-------------|
| Client ID | Yes | The app registration's Application (client) ID. |
| Authentication Method | Yes | `Certificate` (the default), `Federated Credential` or `Client Secret`. |
| Certificate | With Certificate | The certificate and its private key, as a PFX file's contents Base64-encoded. JIM stores it encrypted. It needs an RSA key of at least 2,048 bits; saving warns within 30 days of its expiry. |
| Certificate Password | No | The password protecting the PFX, if it has one. |
| Managed Identity Client ID | With Federated Credential | The client ID of the user-assigned managed identity attached to the Azure virtual machine JIM runs on. A system-assigned identity cannot be a federated credential. |
| Client Secret | With Client Secret | The secret's **Value**, not its **Secret ID**. JIM stores it encrypted. |
| Connection Timeout | No | Seconds to wait for Microsoft Graph or the token service to answer, up to 600. Default 100. |

### Rate Limiting

| Setting | Required | Description |
|---------|----------|-------------|
| Maximum Writes Per Second | No | The most writes JIM sends each second, from 1 to 20. Default 10, which leaves room for the tenant's other applications. |
| Maximum Retries | No | How many times a request that failed for a temporary reason, such as throttling, is retried, from 0 to 10. Default 5. |

### Export

| Setting | Required | Description |
|---------|----------|-------------|
| Delete Behaviour | No | `Delete` (the default) deletes the object; `Disable` disables a user instead. A group is deleted either way. |

## :material-shield-lock-outline: Security Considerations

- **The Connector needs internet access.** JIM must reach `login.microsoftonline.com` and `graph.microsoft.com` on HTTPS (port 443), or the hosts of the tenant's national cloud. Every other part of JIM works without internet access; this Connector cannot, since the tenant is a Microsoft cloud service.
- **Prefer a certificate or a federated credential to a client secret.** A certificate's private key never leaves JIM; a federated credential needs no secret at all.
- **Grant the least the Connected System needs.** Read permissions are enough for import only, and the password permission and role only where JIM sets passwords.
- **Traffic is encrypted.** Microsoft Graph is reached over TLS 1.2 or later. Where a proxy inspects TLS traffic, add its CA certificate to JIM's certificate store under **Admin > Certificates**.

## :material-wrench-outline: Troubleshooting

| What you see | What to do |
|--------------|------------|
| Saving the settings names a missing permission | Add it under the app registration's **API permissions**, as an application permission, and grant admin consent. |
| `AADSTS7000215` | The **Client Secret** is not valid: supply the secret's **Value**, not its **Secret ID**. |
| `AADSTS700027` | The certificate is not on the app registration: upload its public part under **Certificates & secrets**. |
| `AADSTS700213`, `AADSTS70021` | No federated credential matches the managed identity: add one for the identity in **Managed Identity Client ID**. |
| A Delta Import read every object | Its Activity says why; see [Full and Delta Import](#full-and-delta-import). |
| An export refused because the object is synchronised from Active Directory | Scope the export Synchronisation Rule on `sourceOfAuthority`, or write the attribute in Active Directory. |

## :material-book-open-variant-outline: Related

- [Microsoft Entra ID Connector setup](../powershell/entra-id-connector.md): `Register-JIMEntraIdConnector`.
- [Passwords](../concepts/passwords.md) and [Password Synchronisation](../concepts/password-synchronisation.md).
- [Connected Systems](../configuration/connected-systems.md).
