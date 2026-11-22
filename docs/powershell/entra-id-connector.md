---
title: Microsoft Entra ID Connector setup
description: "The cmdlet that sets up the Microsoft Entra ID Connector's app registration, permissions, credential and Connected System, and repairs them."
type: reference
audience: automation-author
---

# Microsoft Entra ID Connector setup

The Microsoft Entra ID Connector reaches a tenant through Microsoft Graph, as an app registration in that tenant. Setting one up by hand means creating the application, choosing its permissions, granting admin consent, making a certificate and copying four values into JIM. `Register-JIMEntraIdConnector` does all of it from one command. Run it again whenever you like: it changes nothing if everything is right, corrects what has drifted, and with `-WhatIf` lists what it would change.

```jim-diagram
archetype: flow
title: What Register-JIMEntraIdConnector sets up, in order
desc: "You sign in to Microsoft Graph. In the Entra ID tenant, Register-JIMEntraIdConnector creates the application, then its service principal, then grants it Microsoft Graph application permissions with admin consent, then gives it a credential: a certificate made in memory, or a federated credential trusting a managed identity. Finally it creates the Connected System in JIM with the tenant, the application and the credential set."
in: { label: Your sign-in, note: may grant admin consent }
out: { label: Connected System, note: in JIM }
container: { label: Entra ID tenant }
steps:
  - { id: app, label: Application }
  - { id: sp, label: Service principal }
  - { id: permissions, label: Permissions, note: with admin consent }
  - { id: credential, label: Credential, note: certificate or managed identity }
```

---

## :material-microsoft-azure: Register-JIMEntraIdConnector

Sets up the Microsoft Entra ID Connector, or repairs its setup.

In the tenant `-TenantId` names, it creates or corrects:

| What | What it is for |
|------|----------------|
| An application (*Name*) | The identity the Connector signs in as. It accepts accounts in this tenant only. |
| Its service principal | What the permissions are granted to. |
| Microsoft Graph application permissions, with admin consent | What the Connector may do, least privilege: see the next table. A permission it no longer needs is revoked. |
| A credential | A certificate, or a federated credential trusting a managed identity: see below. |
| With `-PasswordManagement`, the User Administrator role | Microsoft Graph lets an application set passwords only while it holds this role. It is assigned over the Administrative Unit `-AdministrativeUnitId` names, never across the tenant. |

In JIM, it creates the Connected System (*ConnectedSystemName*) with the tenant, the application and the credential already set, or corrects one that has drifted. Connect to JIM with `Connect-JIM` first.

| Permission | Why the Connector needs it | Left out with |
|------------|----------------------------|---------------|
| `User.ReadWrite.All` | To read and write users. `User.Read.All` with `-ReadOnly`. | |
| `Group.ReadWrite.All` | To read and write groups and their members. `Group.Read.All` with `-ReadOnly`. | |
| `Member.Read.Hidden` | To read the members of groups whose membership is hidden. Anyone who can view those groups in JIM then sees their members. | `-SkipHiddenMembership` |
| `Directory.Read.All` | To offer the tenant's directory extensions in the schema. Only with `-DirectoryExtensions`. | |
| `User-PasswordProfile.ReadWrite.All` | To set passwords, for Initial Password, Set Password and Password Synchronisation. Only with `-PasswordManagement`. | |
| `User-OnPremisesSyncBehavior.ReadWrite.All`, `Group-OnPremisesSyncBehavior.ReadWrite.All` | To tell which objects have had their [Source of Authority](../connectors/jim-graph-connector.md#source-of-authority) switched between Active Directory and Entra ID. Only with `-TrackSourceOfAuthority`, which also ticks the Connected System's **Track Source of Authority Changes**. Microsoft offers no read-only form; JIM never switches an object. | |

**Running it again.** A capability switch you leave out keeps what the setup already does: a later run with only `-TrackSourceOfAuthority` adds that and takes nothing away, and a run with no switches repairs drift without changing what the Connector can do. A Connected System with **Track Source of Authority Changes** ticked in the portal counts as asking for it, so a re-run grants its permissions. To turn a capability off, give its switch as false, such as `-TrackSourceOfAuthority:$false`; the permissions it needed are revoked.

**The credential.** By default it is a certificate this command makes in memory. Its private key goes only to JIM, which stores it encrypted; the application holds only the public part, and nothing is written to disk. A certificate is renewed when it has less than 30 days to run, and the old one is removed once JIM holds the new. Where JIM runs on an Azure virtual machine with a user-assigned managed identity attached, give `-ManagedIdentityClientId` instead: the application trusts that identity with a federated credential, and no secret or key exists anywhere.

**Signing in.** It uses Microsoft Graph, so it runs from any computer that can reach it. Sign in through the browser it opens, as a person who may grant admin consent for application permissions, a Privileged Role Administrator or Global Administrator. The sign-in is an ordinary one, so your tenant's Conditional Access and multifactor authentication policies apply. On a computer with no browser, add `-UseDeviceCode`, where your tenant allows device code sign-in. For running it unattended, pass `-AccessToken` with a Graph token holding `Application.ReadWrite.All` and `AppRoleAssignment.ReadWrite.All`, and `RoleManagement.ReadWrite.Directory` with `-PasswordManagement`.

<!-- BEGIN GENERATED: Register-JIMEntraIdConnector -->
### Syntax

```powershell
# SignIn (default)
Register-JIMEntraIdConnector -TenantId <string> [-UseDeviceCode] [-Name <string>] [-ConnectedSystemName <string>] [-ReadOnly] [-SkipHiddenMembership] [-DirectoryExtensions] [-PasswordManagement] [-TrackSourceOfAuthority] [-AdministrativeUnitId <string>] [-ManagedIdentityClientId <string>] [-CertificateValidityDays <int>] [-ResetCertificate] [-WhatIf] [-Confirm]

# Token
Register-JIMEntraIdConnector -AccessToken <SecureString> [-Name <string>] [-ConnectedSystemName <string>] [-ReadOnly] [-SkipHiddenMembership] [-DirectoryExtensions] [-PasswordManagement] [-TrackSourceOfAuthority] [-AdministrativeUnitId <string>] [-ManagedIdentityClientId <string>] [-CertificateValidityDays <int>] [-ResetCertificate] [-WhatIf] [-Confirm]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `TenantId` | `string` | Yes (SignIn) |  | The tenant to set the Connector up in: its tenant ID or one of its verified domain names, such as contoso.onmicrosoft.com. |
| `UseDeviceCode` | `switch` | No (SignIn) |  | Signs in to Microsoft Graph with a device code instead of a browser, for a console with no browser. Microsoft recommends that Conditional Access blocks device code sign-in, so it fails in many tenants. |
| `AccessToken` | `SecureString` | Yes (Token) |  | A Microsoft Graph access token to use instead of signing in, holding Application.ReadWrite.All and AppRoleAssignment.ReadWrite.All, and RoleManagement.ReadWrite.Directory with `-PasswordManagement`. |
| `Name` | `string` | No | `'JIM Entra ID Connector'` | The application's display name, by which it is found. |
| `ConnectedSystemName` | `string` | No | `'Microsoft Entra ID'` | The name of the Connected System in JIM, by which it is found. |
| `ReadOnly` | `switch` | No |  | Asks only to read users and groups, for a Connected System that imports and never exports. |
| `SkipHiddenMembership` | `switch` | No |  | Leaves out Member.Read.Hidden. Without it, the members of groups whose membership is hidden are never read, and JIM leaves them as it holds them; with it, anyone who can view those groups in JIM sees their members. |
| `DirectoryExtensions` | `switch` | No |  | Adds Directory.Read.All, so a schema refresh can offer the tenant's directory extensions. |
| `PasswordManagement` | `switch` | No |  | Lets JIM set passwords in Entra ID: its Initial Password, Set Password and Password Synchronisation. Adds User-PasswordProfile.ReadWrite.All and assigns the application the User Administrator role over the Administrative Unit `-AdministrativeUnitId` names, which Microsoft Graph also requires. |
| `TrackSourceOfAuthority` | `switch` | No |  | Turns on the Connected System's Track Source of Authority Changes, and adds the User-OnPremisesSyncBehavior.ReadWrite.All and Group-OnPremisesSyncBehavior.ReadWrite.All permissions it needs, so an object whose Source of Authority was switched between Active Directory and Entra ID reads as managed where it now is. Microsoft offers no read-only form of these permissions: they also allow switching an object's Source of Authority, which JIM never does. |
| `AdministrativeUnitId` | `string` | No |  | With `-PasswordManagement`, the object ID of the Administrative Unit holding the users JIM manages. The User Administrator role is assigned over it only, never across the tenant. A later run that leaves `-PasswordManagement` out keeps the permission and leaves the role assignment as it is. |
| `ManagedIdentityClientId` | `string` | No |  | The client ID of the user-assigned managed identity attached to the Azure virtual machine JIM runs on. The application trusts it with a federated credential, and no certificate is made. A later run that leaves it out keeps the managed identity the Connected System already uses. |
| `CertificateValidityDays` | `int` | No | `365` | How long a certificate this command makes is valid, in days. From 30 to 730. |
| `ResetCertificate` | `switch` | No |  | Makes a new certificate even though the current one is still valid, and removes the old one once JIM holds the new. On a Connected System that authenticates with a managed identity, it moves it back to a certificate. |

### Output

With `-WhatIf`, nothing is returned when there is something to change; when everything is already right, the result comes back with no Changes.

| Property | Type | Description |
|----------|------|-------------|
| `TenantId` | `string` | The tenant's ID. |
| `ClientId` | `string` | The application's client ID. |
| `ConnectedSystemId` | `int` | The Connected System's ID in JIM. |
| `AuthenticationMethod` | `string` | Certificate or Federated Credential. |
| `CertificateThumbprint` | `string` | The thumbprint of a certificate this run made. Empty when it made none. |
| `CertificateExpires` | `datetime` | When a certificate this run made expires. Empty when it made none. |
| `Permissions` | `string[]` | The Microsoft Graph application permissions the application holds. |
| `Changes` | `string[]` | The changes this run made. Empty when everything was already right. |
<!-- END GENERATED: Register-JIMEntraIdConnector -->

### Examples

```powershell title="Set up the Connector in a tenant"
Connect-JIM -Url 'https://jim.example.com'
$setup = Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com'
$setup | Select-Object ClientId, ConnectedSystemId, CertificateExpires
```

```powershell title="Check the setup for drift, changing nothing"
Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -WhatIf
```

```powershell title="Let JIM set passwords, over one Administrative Unit"
Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -PasswordManagement -AdministrativeUnitId '9d6f4c2e-1b3a-4e5f-8a7b-6c5d4e3f2a1b'
```

```powershell title="Track objects whose Source of Authority is switched, keeping the rest of the setup"
Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -TrackSourceOfAuthority
```

```powershell title="Authenticate with the virtual machine's managed identity"
Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -ManagedIdentityClientId '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'
```

```powershell title="Replace the certificate early"
Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -ResetCertificate
```

If a run fails after making a certificate but before JIM receives it, the command warns that the key is lost; run it again with `-ResetCertificate` once the cause is fixed.
