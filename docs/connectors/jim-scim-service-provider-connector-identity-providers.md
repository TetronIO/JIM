---
title: "JIM SCIM 2.0 Service Provider Connector: Identity Providers"
description: "Point an identity provider, or another JIM, at a Connected System's SCIM endpoint: where its base URL and key go, and how to tell it worked."
type: how-to
audience: administrator
---

# JIM SCIM 2.0 Service Provider Connector: Identity Providers

An identity provider needs two things from JIM to push Users and Groups to it: the base URL of a Connected System's SCIM endpoint, and a key. Do this once for each identity provider, after creating its Connected System with the [JIM SCIM 2.0 Service Provider Connector](jim-scim-service-provider-connector.md); afterwards the identity provider sends its changes as they happen, and JIM imports them on the Connected System's Schedule.

## Before you begin

- A Connected System using the JIM SCIM 2.0 Service Provider Connector, with its schema imported and the **User** and **Group** Object Types selected ([Create a Connected System for an identity provider](jim-scim-service-provider-connector.md#create-a-connected-system-for-an-identity-provider)).
- Its base URL, from the **SCIM** tab, and a key created there. The key is shown once; if it is lost, create another.
- JIM reachable from the identity provider over HTTPS, with a certificate the identity provider trusts. A cloud identity provider calls JIM from the internet, so it can reach a JIM behind your firewall only through whatever on-premises agent it offers; check its documentation for one before opening JIM to the internet.

## Connect the identity provider

1. In the identity provider, start a new SCIM 2.0 provisioning target (an application, a connector or an outbound connection, depending on the product), choosing a custom or generic SCIM application rather than a named one.
2. Paste the base URL into its endpoint field.
3. Choose bearer token authentication, and paste the key into its token field. The identity provider sends it as `Authorization: Bearer <key>`.
4. Match Users on `userName`, which is unique within the Connected System. Map the identity provider's own immutable identifier to `externalId` if it offers to; that is unique as well, and lets you tell which of its accounts a User came from.
5. Turn off password provisioning. JIM refuses a password sent over SCIM, so leaving it on fails every create that carries one.
6. Choose which Users and Groups the identity provider sends, using its own assignment or scoping settings.
7. Test the connection, and start provisioning.

Where the base URL and key go in particular identity providers:

| Identity provider | Base URL | Key |
|---|---|---|
| Microsoft Entra ID | An enterprise application created as a non-gallery application: **Provisioning**, Provisioning Mode **Automatic**, **Tenant URL** | **Secret Token**, in the same **Admin Credentials** section; **Test Connection** checks both |
| Another JIM | A Connected System using the [JIM SCIM 2.0 Client Connector](jim-scim-connector.md): **Base URL** | **Authentication Method** set to **Static Bearer Token**, and the key in **Bearer Token** |

The labels in another product's portal are its own and can change; its documentation is authoritative for them. Any identity provider that sends a static bearer token to a SCIM 2.0 endpoint works in the same way.

### From another JIM

A JIM using the JIM SCIM 2.0 Client Connector is an identity provider like any other, and also reads back what it sent:

1. Trust the certificate the receiving JIM serves, if a public certificate authority did not issue it: add its certificate authority on the sending JIM's [Certificates](../configuration/certificates.md) page, trusted for Connectors.
2. On the sending Connected System's Settings tab, set **Base URL** to the receiving Connected System's base URL, **Authentication Method** to **Static Bearer Token**, **Bearer Token** to the key, and **Certificate Validation** to **Full Validation**. Saving the settings tests the connection.
3. Import the schema. Because the receiving JIM supports entity tags, `meta.version` is selected and locked on every Object Type, so the sending JIM's updates carry `If-Match` and cannot overwrite a change the receiving side's own identity providers made.
4. Flow the sending JIM's Users and Groups to it through outbound Synchronisation Rules, and export.

## Check it worked

1. On the receiving Connected System's **SCIM** tab, the key's **Last Used** time shows when and from where the identity provider first called, and the **Store** panel counts the Users and Groups it has sent.
2. **Awaiting Import** is above zero until the next Delta Import, then returns to zero.
3. Run the Connected System's **Delta Import** and **Delta Synchronisation** (or wait for its Schedule): the Connector Space holds the Users and Groups the identity provider sent, joined to or projected as Metaverse Objects.
4. Filter the Activity list by **SCIM Requests**. The Activity for the key counts what the identity provider created, and names the latest request JIM refused, if any; [Troubleshooting](jim-scim-service-provider-connector.md#troubleshooting) explains each refusal.

## Next steps

- [Schedule the import](jim-scim-service-provider-connector.md#schedule-the-import), so the identity provider's changes reach the Metaverse within minutes.
- [Rotate a key](jim-scim-service-provider-connector.md#rotate-a-key) before it expires: a key always does, at most two years after it was created.
