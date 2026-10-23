---
title: JIM SCIM 2.0 Service Provider Connector
description: "Let identity providers and other SCIM 2.0 clients push Users and Groups to JIM's own SCIM endpoint, and import what they send on a Schedule."
type: object
audience: administrator
---

# JIM SCIM 2.0 Service Provider Connector

The JIM SCIM 2.0 Service Provider Connector lets an identity provider push Users and Groups to JIM over [SCIM 2.0](https://datatracker.ietf.org/doc/html/rfc7644), instead of JIM reading them from it. Each Connected System that uses this Connector has a SCIM endpoint of its own: the identity provider writes to it with a key JIM gives it, JIM keeps what it writes, and the Connector imports those Users and Groups into the Connector Space on a Schedule, from where Synchronisation Rules carry them on like any other source.

!!! info "Preview"
    The SCIM 2.0 Service Provider is a [Preview feature](../administration/preview-features.md). Turn on **SCIM 2.0 Service Provider** on the Service Settings page (or run `Enable-JIMFeature -Name "Features.ScimServiceProvider"`) before creating a Connected System with this Connector.

!!! note "Direction matters"
    This Connector is for systems that **push** to JIM. To have JIM read from and write to a system that publishes a SCIM interface of its own, use the [JIM SCIM 2.0 Client Connector](jim-scim-connector.md).

## From request to Metaverse

Every request an identity provider sends is answered straight away, from a store JIM keeps for that Connected System: a create, change or delete is checked against the Connected System's SCIM schemas, kept, and acknowledged in the same request. Nothing the identity provider sends touches the Connector Space or the Metaverse directly.

--8<-- "assets/diagrams/scim-service-provider.svg"

<p class="jim-diagram-caption">The request is answered and kept as it arrives; the Delta Import and Delta Synchronisation run on the Connected System's Schedule.<span class="jimdg-caption-motion"> Moving dots trace a change on its way to the Metaverse.</span></p>

The Connector imports from that store through ordinary Full Import and Delta Import Run Profiles, and synchronisation then joins or projects what it imported, exactly as it does for a directory or a database. A change therefore reaches the Metaverse at the next scheduled Delta Import and Delta Synchronisation of the Connected System, not the moment the identity provider sends it; how often you schedule them decides the delay (see [Schedule the import](#schedule-the-import)).

Because the store holds everything the identity provider sent, the Connector Space can be rebuilt from it at any time without the identity provider sending anything again.

## Features

| Capability | Supported | Notes |
|---|---|---|
| Full Import | ✅ | Reads every User and Group the store holds. A resource the store no longer holds is found deleted by its absence. |
| Delta Import | ✅ | Reads every change since the last completed import, deletions included. |
| Export | ❌ | The identity provider owns what it sends. JIM writes nothing back to the store. |
| Paging | ✅ | The Run Profile's Page Size sets how many resources each page reads. |
| Partitions and Containers | ❌ | SCIM has no partition concept; resource types are Connected System Object Types. |
| Passwords | ❌ | A password an identity provider sends is refused, never stored. |
| Probe | ❌ | Nothing to probe: the values are already in the store the Connector imports. |

### Schema

The store is created with the SCIM 2.0 core schemas ([RFC 7643](https://datatracker.ietf.org/doc/html/rfc7643)): the User resource type with the Enterprise User extension, served at `/Users`, and the Group resource type, served at `/Groups`. Importing the schema presents them as the **User** and **Group** Object Types, with the same flattened attribute names the [JIM SCIM 2.0 Client Connector](jim-scim-connector.md#schema-discovery) uses (`name.givenName`, `emails.work`, `enterpriseUser.department`). The schemas are read-only: a resource type, extension or attribute cannot be added to them.

The External ID is always `id`, which JIM assigns when the identity provider creates a resource and never changes. Attribute Flows on this Connected System are offered the SCIM 2.0 [Standard Mappings](../configuration/metaverse.md#standard-mappings).

The store keeps everything the identity provider sends that the schemas declare, whichever attributes you select. Selecting another attribute later and running a Full Import fills it in for every User and Group, without the identity provider sending them again.

## What the endpoint accepts

The endpoint's base URL is `https://<your JIM host>/scim/v2/<Connected System ID>`. The SCIM tab of the Connected System shows it with a **Copy** button.

| Endpoint | Methods |
|---|---|
| `/ServiceProviderConfig` | `GET` |
| `/ResourceTypes`, `/ResourceTypes/{id}` | `GET` |
| `/Schemas`, `/Schemas/{id}` | `GET` |
| `/Users`, `/Groups` | `POST` to create, `GET` to list or filter, `POST .search` |
| `/Users/{id}`, `/Groups/{id}` | `GET`, `PUT`, `PATCH`, `DELETE` |

- **HTTPS only**<br /> JIM does not accept a SCIM request over plain HTTP. Where TLS ends at a reverse proxy, [trust the proxy](../administration/tls-and-reverse-proxy.md#trusting-the-reverse-proxy) so JIM sees the request arrived over HTTPS.
- **Filters**<br /> `eq`, `co` and `sw` on text attributes, `eq` on true or false attributes and on `id`, and `eq`, `gt`, `ge`, `lt` and `le` on `meta.lastModified`, joined with `and` and `or`. A filter outside those is refused with `invalidFilter`. Text compares ignoring case unless the schema declares the attribute `caseExact`.
- **Paging**<br /> 100 resources a page unless the client asks for a `count`, and at most 1,000. Both index paging (`startIndex`) and cursor paging ([RFC 9865](https://datatracker.ietf.org/doc/html/rfc9865)) work.
- **Entity tags**<br /> Every resource carries a version, sent as its `ETag` and its `meta.version`. A `PUT`, `PATCH` or `DELETE` that sends `If-Match` with an older version is refused with `412`, so a client cannot overwrite a change it has not seen.
- **Not offered**<br /> `/Bulk`, `/Me`, sorting and password changes. `/ServiceProviderConfig` says so, and a client that asks anyway gets `404` for `/Bulk` and `/Me`, and results in a fixed order for sorting.

Identity providers do not all follow the specification to the letter, so the endpoint accepts what they commonly send instead:

- an `Authorization` scheme in any case (`bearer`), or the key alone with no scheme;
- a trailing slash on the base URL, and query parameters it does not know;
- a PATCH `op` in any case, true and false sent as text, and a one-entry array where one value is expected;
- a PATCH `remove` naming a multi-valued attribute with a list of values, which removes only those values rather than all of them;
- the check some identity providers make before every cycle, a filter on a `userName` that cannot exist, answered with an empty list.

An attribute the schemas do not declare is ignored rather than refused, so a mapping an administrator has added at the identity provider cannot fail every create. Ignored is never silent: the history of each request names the attributes it ignored (see [See what an identity provider sent](#see-what-an-identity-provider-sent)).

## What happens to each change

| The identity provider | The store | JIM, after the next Delta Import and Delta Synchronisation |
|---|---|---|
| Creates a User or Group | Keeps it with a new `id` | A new Connected System Object, joined to an existing Metaverse Object by an [Object Matching Rule](../configuration/object-matching-rules.md) or projected as a new one |
| Changes it (`PUT` or `PATCH`) | Applies the change and raises its version | The Connected System Object is updated and Attribute Flows run |
| Sets `active` to false | Keeps the value | An ordinary attribute change: whatever your Synchronisation Rules do with `active` happens |
| Changes a Group's members | Applies only the members named | The Group's full membership is imported |
| Deletes a User or Group | Marks it deleted, frees its `userName`, and removes it from every Group that held it | The Connected System Object is deleted, and the Metaverse Object's [deletion rules](../configuration/metaverse.md#deletion-behaviour) apply |
| Creates a User again after deleting it | Keeps it as a new resource with a new `id` | A new Connected System Object, which joins the existing Metaverse Object if it still exists |

A change that changes nothing (the same values sent again, members in another order) is not kept as a change and gives the next import nothing to do. Deletions are held for the Connector to read: one is cleared away only once an import has read it and seven days have passed.

To keep a Metaverse Object when an identity provider deletes a User and creates it again, give its object type a [grace period](../configuration/metaverse.md#grace-period).

## Create a Connected System for an identity provider

Create one Connected System for each identity provider, and for each tenant of one. Each has its own base URL, keys, uniqueness of `userName` and Connector Space, so two identity providers can never overwrite each other. The same person arriving from both becomes two Users that join one Metaverse Object through an Object Matching Rule, as an HR source and a directory do.

1. Turn on the **SCIM 2.0 Service Provider** Preview feature.
2. Create a Connected System and choose **JIM SCIM 2.0 Service Provider Connector**. It has no settings to fill in.
3. On the **Schema** tab, import the schema and select the **User** and **Group** Object Types and the attributes you will flow.
4. On the **SCIM** tab, use **Create Key**, name it after the identity provider that will hold it, and copy the key from the dialog. It is shown once.
5. Give the identity provider the base URL and the key. [Identity Providers](jim-scim-service-provider-connector-identity-providers.md) says where each goes.
6. Add Object Matching Rules and inbound Synchronisation Rules, create **Full Import**, **Delta Import**, **Full Synchronisation** and **Delta Synchronisation** Run Profiles, and [schedule the import](#schedule-the-import).

From PowerShell, steps 2 and 4 are:

```powershell
$connector = Get-JIMConnectorDefinition -Name "JIM SCIM 2.0 Service Provider Connector"
$system = New-JIMConnectedSystem -Name "Entra ID" -ConnectorDefinitionId $connector.Id -PassThru
New-JIMScimKey -ConnectedSystemId $system.Id -Name "Entra provisioning 2026"
```

!!! warning "A different tenant needs a different Connected System"
    Never point an existing Connected System at another identity provider or tenant. JIM cannot rebuild what the first one sent, so until the new one has sent everything, the first one's Users and Groups look deleted. Create a new Connected System instead, and delete the old one once nothing depends on it.

## Schedule the import

Add the Connected System's **Delta Import** and **Delta Synchronisation** to a [Schedule](../configuration/schedules.md), as Sequential steps in that order, followed by the exports of the Connected Systems they feed. An interval of a few minutes keeps the delay between an identity provider's change and the Metaverse to about that interval; the import of an unchanged store reads nothing and finishes in moments.

A Delta Import reads every deletion since the last import, so a regular Schedule needs no Full Import. In the rare case that a Delta Import cannot be sure it would see every deletion, it fails and asks for a Full Import, rather than leaving deleted Users behind; run the Full Import, then let the Schedule carry on.

## Rotate a key

A key works only on the SCIM endpoint of its own Connected System, carries no Roles, and always expires, at most two years after it is created. A Connected System can have two usable keys, so a key can be replaced without a gap:

1. On the **SCIM** tab, use **Create Key** and give the new key to the identity provider.
2. Wait until the old key's **Last Used** time stops moving.
3. **Disable** the old key, or **Delete** it.

A disabled key can be enabled again; a deleted one cannot. The same steps from PowerShell are on [SCIM Service Provider](../powershell/scim-service-provider.md#set-jimscimkey).

## See what an identity provider sent

- **SCIM requests, as Activities**<br /> Each key's requests are summarised in one Activity per 15 minutes: how many Users and Groups it created, updated and deleted, and how many requests JIM refused, with the latest refusal named in the Activity's warning. Filter the Activity list by **SCIM Requests**, or run `Get-JIMActivity -Operation Receive`. Reads are not counted, and neither are requests refused for their key, which are [security audit events](../administration/security-audit-events.md).
- **The store**<br /> The **Store** panel on the SCIM tab counts the Users and Groups held, the changes awaiting import, the deletions held, the requests refused in the last 24 hours, and the last write. `Get-JIMScimStoreSummary` returns the same.
- **The requests behind a change**<br /> On a Connected System Object's change history, the details of an imported change list the SCIM requests that made it: when, what each changed or ignored, which key sent it and from where. `Get-JIMScimJournal -ChangeId` returns the same, and `Get-JIMScimJournal -ResourceId` returns every request for one User or Group. This history is kept for the [History Retention Period](../administration/configuration.md#service-settings).

## Rebuild the Connector Space from the store

[Clearing the Connector Space](../configuration/connector-space.md#clearing-the-connector-space) of this Connected System leaves the store untouched: every User and Group the identity provider sent is still there. Run a **Full Import** and a **Full Synchronisation** afterwards, and the Connector Space is rebuilt from the store without the identity provider sending anything. Run the Full Import before the next Delta Import, which reads only changes and would not bring back what was cleared.

Deleting the Connected System, by contrast, deletes its store and its keys with it. Nothing the identity provider sent can be recovered, and the identity provider's requests are refused from that moment.

## Settings

The Connector has no settings of its own. The rules that govern its endpoint:

| Rule | Value | Where it is set |
|---|---|---|
| Requests per minute, for each Connected System | `1500`, shared by all its keys. Over the limit, a client gets `429` with `Retry-After`. | The `Security.RateLimiting.ScimServiceProviderRequestsPerMinute` Service Setting ([Rate Limiting](../api/rate-limiting.md)) |
| Usable keys, for each Connected System | 2 | Fixed |
| Key lifetime | 365 days unless you choose; at most 730 | Chosen when the key is created |
| Page size | 100 unless the client asks; at most 1,000 | Fixed |
| Deletions held after an import has read them | 7 days | Fixed |

## Troubleshooting

**The identity provider gets `401` with "A valid API key for this SCIM endpoint is required."**
: The key is wrong, disabled, expired, deleted, or belongs to another Connected System; JIM gives the same answer for each, so the identity provider learns nothing about which. The [security audit events](../administration/security-audit-events.md) record the real reason. Check the key's status and expiry on the SCIM tab, and that the base URL ends in this Connected System's ID; create a new key if it is lost. Nothing the identity provider sent before is affected.

**The identity provider gets `403` with "SCIM requests must use HTTPS."**
: The request reached JIM over plain HTTP. Give the identity provider an `https://` base URL; where TLS ends at a reverse proxy, set `JIM_TRUSTED_PROXIES` to that proxy ([Trusting the Reverse Proxy](../administration/tls-and-reverse-proxy.md#trusting-the-reverse-proxy)).

**The identity provider gets `404` for every request.**
: The SCIM 2.0 Service Provider feature is off. Turn it on; everything the store held is still there.

**The identity provider gets `409` with `uniqueness`.**
: Another User on this Connected System already has that `userName` (compared ignoring case), or that `externalId`. Usually the identity provider has lost track of a User it created before and is creating it again. Find the existing User with `Get-JIMScimJournal` or in the Connector Space, then either have the identity provider delete it, or correct the value at the identity provider. Nothing is written for the refused request.

**The identity provider gets `400` with `invalidValue` for a password.**
: JIM never accepts a password through SCIM, and keeps nothing of one it refuses. Turn off password provisioning in the identity provider's SCIM settings for JIM.

**The identity provider gets `429`.**
: It is sending more requests a minute than the Connected System allows. A well-behaved client waits for `Retry-After` and continues; raise the limit if its initial load needs to go faster.

**A Delta Import fails with "This Delta Import cannot run".**
: The Connector cannot be sure it would see every deletion. Run a Full Import, which finds every deletion by what it no longer reads; the next Delta Import then runs normally.

**Imports fail with "...feature, which is turned off."**
: The SCIM 2.0 Service Provider feature was turned off after the Connected System was created. Turn it on again; nothing was lost.

**The identity provider's changes take a long time to reach the Metaverse.**
: Changes are imported on the Connected System's Schedule, not as they arrive. Check **Awaiting Import** on the SCIM tab: above zero, the Schedule has not run since the change; shorten its interval or check that it is enabled.

## Related

- [Identity Providers](jim-scim-service-provider-connector-identity-providers.md): where the base URL and key go in each identity provider
- [SCIM Service Provider cmdlets](../powershell/scim-service-provider.md): keys, the store summary and the request history from PowerShell
- [JIM SCIM 2.0 Client Connector](jim-scim-connector.md): JIM calling a system's SCIM interface, the opposite direction
- [Preview Features](../administration/preview-features.md)
- [Schedules](../configuration/schedules.md)
