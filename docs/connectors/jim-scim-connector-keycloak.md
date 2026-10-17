---
title: Connect the SCIM Connector to Keycloak
description: Provision users and groups into a Keycloak realm, and import them from it, through Keycloak's built-in SCIM 2.0 API.
type: how-to
audience: administrator
---

# Connect the SCIM Connector to Keycloak

This guide connects the [JIM SCIM 2.0 Client Connector](jim-scim-connector.md) to a Keycloak realm through Keycloak's built-in SCIM 2.0 API, so JIM can provision users and groups into the realm and import them from it. At the end, a Full Import brings the realm's users and groups, with their members, into JIM.

Keycloak's SCIM API differs from the SCIM specification in several ways that change how a Connected System has to be set up: it does not stamp every change with a new last-modified date, it normalises some values, and it hides some objects. Each step below says which difference it accounts for.

## Before you begin

- **Keycloak 26.8.0 or later.** 26.8.0 made the SCIM API supported. Earlier versions differ in the roles discovery needs, in how they check a token's audience, and in how many changes one request may carry, so this guide does not apply to them.
- **An administrator account on the realm** JIM will connect to, to turn the SCIM API on and create a client.
- **HTTPS.** The Connector refuses a plain HTTP Base URL for anything but a loopback address. If Keycloak's certificate is issued by a private certificate authority, have that certificate to hand; the connection test offers to trust it.
- **The address Keycloak calls itself.** Keycloak checks that a token was issued for its SCIM API at the address it knows itself by: its frontend hostname (`KC_HOSTNAME`). If JIM reaches Keycloak through another name, you need both.

## 1. Turn on the realm's SCIM API

In the Keycloak Admin Console, select the realm, open **Realm settings**, and on the **General** tab turn **SCIM API** on.

The realm's SCIM Base URL is then `https://<host>/realms/<realm>/scim/v2`, where `<host>` is Keycloak's frontend hostname.

## 2. Create a client for JIM

In **Clients**, create an OpenID Connect client for JIM:

1. **Client authentication** on (a confidential client). Keycloak refuses a public client's token with 403 on every SCIM call.
2. **Service accounts roles** on, and every other authentication flow off.
3. On the client's **Client scopes** tab, open its dedicated scope and turn **Full scope allowed** off. Keycloak deprecates it, and turning it off keeps the token to the roles granted below.
4. Grant the `realm-management` client role `manage-users` **twice**: on the client's **Service accounts roles** tab, and in the dedicated scope's **Scope** tab. A role missing from either place produces 403 on every call.
5. In the dedicated scope, add a mapper **By configuration** > **Audience**, with **Included Custom Audience** set to the exact Base URL from step 1, as Keycloak's frontend hostname gives it, and **Add to access token** on.

Note the client's **Client ID**, and its **Client Secret** from the **Credentials** tab.

## 3. Create the Connected System

Create a Connected System with the JIM SCIM 2.0 Client Connector and these settings:

| Setting | Value | Why |
|---|---|---|
| Base URL | `https://<host>/realms/<realm>/scim/v2` | The address JIM calls. It may use another hostname from the audience in step 2, provided it reaches the same Keycloak. |
| Authentication Method | OAuth 2.0 Client Credentials | |
| Token Endpoint URL | `https://<host>/realms/<realm>/protocol/openid-connect/token` | |
| Client ID, Client Secret | From step 2 | |
| Change Detection | Full Scan | Keycloak moves a resource's last-modified date only when it is created, replaced, or (for a user) changed through SCIM PATCH. A filter on it would miss group membership changes, group renames, and anything changed in the Admin Console, the Admin REST API, LDAP federation or by the user. JIM recognises Keycloak and reads everything anyway, warning on each Delta Import; choosing Full Scan stops the warning. |
| Use Bulk Operations | Off | Keycloak does not offer `/Bulk`. |

Test the connection, then import the schema and select the `User` and `Group` Object Types. The schema import warns that Keycloak's schema does not say what is inside a Group's `members`, or a User's `groups` and `emails`, and that JIM used the SCIM specification's definition instead. That is expected, and is what makes those attributes available.

## 4. Shape the Attribute Flows to what Keycloak keeps

Keycloak changes or drops some values it is sent. JIM confirms every export by reading the value back, so a value Keycloak alters is never confirmed, and the export fails after its retries. Flow values Keycloak will keep:

- **Lowercase `userName` and email addresses.** Keycloak stores both in lower case: `Eve.Mixed` is stored as `eve.mixed`. Flow them through an [expression](../configuration/attribute-mappings.md#expression-mappings) such as `Lower(mv["Account Name"])`.
- **Flow one email address.** Keycloak keeps one email per user and drops any others without saying so.
- **Leave the User `displayName` unmapped.** Keycloak does not store it.
- **Take membership from Groups.** Import and export group membership through the Group's `members` attribute, never the User's `groups`: a User's `groups` includes memberships inherited through subgroups.
- **`externalId` on Users** needs a User Profile attribute mapped to it in Keycloak before Keycloak keeps it.

## 5. Scope out accounts Keycloak protects

Keycloak protects every user and group that holds an administrative role (any `realm-management` role) from changes made through SCIM: each write to one is refused with 403, and an import returns it with only its `userName`. Scope those objects out of the Synchronisation Rules that export to this Connected System, so JIM does not attempt changes Keycloak will always refuse.

## 6. Run a Full Import

Run a Full Import Run Profile.

JIM asks Keycloak for a Group's `members` by name, because Keycloak returns them no other way; groups therefore arrive with their members.

## Check it worked

- The Full Import's Activity lists the realm's users and groups, and a group's Connected System Object shows its members.
- The Activity carries no warning about the service provider, apart from the expected one about Change Detection if you left it on Auto-detect.
- Export a test group with a member. Keycloak refuses a group created with members, so JIM creates the group first and adds its members with a second request; the Pending Export confirms at the next import.

## What Keycloak's SCIM API cannot do

These are limits of Keycloak, not of the Connector, and no setting changes them:

- **Subgroups are invisible.** Keycloak lists only top-level groups, so a subgroup is never imported. A group JIM provisioned that is later moved under a parent in Keycloak drops out of the listing, and the next Full Import treats it as deleted.
- **Service accounts are hidden,** and so are federated users Keycloak has not yet imported from their LDAP directory.
- **Realms that write users to LDAP are unsupported for provisioning.** Where the realm's LDAP provider syncs registrations, a user created through SCIM can be stored only in LDAP, after which Keycloak cannot find it through SCIM again, and the confirming import never sees it.
- **Clearing a group's whole membership in one step does nothing.** Keycloak answers success to a request removing every member at once, or replacing the members with none, and changes nothing. JIM removes members one at a time, which works.

## Troubleshooting

**401 "Invalid token audience"**<br />
The Audience mapper from step 2 is missing, or its value is not exactly the Base URL as Keycloak's frontend hostname gives it.

**401 "Bearer token required"**<br />
Despite the wording, the token was issued under a different hostname from Keycloak's frontend hostname. Use a Token Endpoint URL on the frontend hostname.

**403 on every call**<br />
The client is public, or `manage-users` is missing from its service account roles or from its dedicated scope. See step 2.

**404 on discovery**<br />
The realm's SCIM API is off. See step 1.

**403 on some objects only**<br />
Keycloak protects them. See step 5.

**Exports of users never confirm**<br />
A `userName` or email address is being flowed in mixed case. See step 4.

## Related

- [JIM SCIM 2.0 Client Connector](jim-scim-connector.md)
- [SSO Setup: Keycloak](../administration/sso-setup/keycloak.md), for signing in to JIM with Keycloak accounts
