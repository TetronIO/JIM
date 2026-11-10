---
title: Synchronise Global Address Lists Between Forests
description: "Show each Active Directory forest's people and mail-enabled groups as mail contacts in the other forests' Exchange address books."
type: how-to
audience: administrator
---

# Synchronise Global Address Lists Between Forests

Where an organisation runs Exchange in more than one Active Directory forest, each forest's address book shows only its own people. Synchronising the Global Address Lists puts everyone in every address book: each forest's people and mail-enabled groups appear in the other forests as mail contacts, kept up to date as they change and removed when they leave. Each contact carries an `X500:` address made from its source's original Exchange identity, so a reply to an old message, or an address a mail client has cached, still reaches the person. Follow these steps once per pair of forests; the steps name two forests, Forest A and Forest B, and are done for each direction.

```jim-diagram
archetype: map
title: How two forests' address books meet in the Metaverse
desc: "Each forest's people are imported into the Metaverse and joined to their accounts there. An export Synchronisation Rule for each forest sends the people who have an account in the other forest, and none in this one, back out as mail contacts in this forest's GAL container. So a person from Forest A has an account in Forest A and a mail contact in Forest B, and a person from Forest B has an account in Forest B and a mail contact in Forest A. The contacts JIM creates stay joined to the export rule that made them, so the forest's own contact import never projects them as new people."
nodes:
  - { id: auser, label: "`user` in Forest A", row: 0, col: 0 }
  - { id: aperson, label: Person from Forest A, row: 0, col: 1 }
  - { id: bcontact, label: Mail contact in Forest B, row: 0, col: 2 }
  - { id: acontact, label: Mail contact in Forest A, row: 1, col: 0 }
  - { id: bperson, label: Person from Forest B, row: 1, col: 1 }
  - { id: buser, label: "`user` in Forest B", row: 1, col: 2 }
edges:
  - { from: auser, to: aperson, label: imported }
  - { from: aperson, to: bcontact, label: exported }
  - { from: buser, to: bperson, label: imported }
  - { from: bperson, to: acontact, label: exported }
```

## :material-clipboard-list-outline: Before you begin

- **One LDAP Connected System per forest, each managing Exchange recipients**<br /> **Manage Exchange recipients** ticked and an **Exchange server** saved on each, as [Turn on Exchange recipient management](jim-ldap-connector-exchange.md#turn-on-exchange-recipient-management) describes, and the schema refreshed since.
- **Each forest's people in the Metaverse, joined to their own accounts**<br /> An import Synchronisation Rule on each forest's `user` Object Type that projects or joins them, so every person is connected to their account in their own forest. Do the same for groups if you want mail-enabled groups in the other address books.
- **A container for the contacts in each forest**<br /> An organisational unit that holds only the contacts JIM creates, such as `OU=GALSync,OU=Corp`.
- **The rights to create contacts there, and an Exchange role**<br /> The Connected System's account needs to create and delete `contact` objects in that container, and Exchange's Recipient Management role group; see [LDAP Connector Service Account Permissions](jim-ldap-connector-permissions.md#exchange-recipients).

## :material-account-switch-outline: Set up the contacts

Do steps 1 to 6 on each forest's Connected System. Where a step names the other forest, it means the forest whose people the contacts are for.

1. **Bring back each recipient's Exchange identity.** On the Schema tab, select `legacyExchangeDN`, `proxyAddresses` and `mail` on `user` (and on `group`, if you include groups). On the forest's import Synchronisation Rule, flow them to the Metaverse Attributes **Legacy Exchange DN**, **Proxy Addresses** and **Email**; the [Standard Mappings](../configuration/metaverse.md#standard-mappings) offer each pairing. Exchange stamps these when it makes a recipient, so a person gets them on the first import after their mailbox exists.

2. **Select the contact container and the `contact` Object Type.** On the Connected System's **Scope** tab, select the container for the contacts: JIM refuses to export anything outside the selected containers. On the Schema tab, select the `contact` Object Type with `cn`, `displayName`, `proxyAddresses` and its Exchange attributes.

3. **Create the export Synchronisation Rule for people.** Direction **Export**, Connected System Object Type `contact`, Metaverse Object Type your people's type, provisioning on, and the Deprovisioning Action **Delete**, so a contact goes when its person leaves the other forest. See [Synchronisation Rules](../configuration/synchronisation-rules.md).

4. **Scope it to the other forest's mail recipients.** On the Scope tab, add an **All** group with three criteria:
    - a **Connection**: **Has** `user` in the other forest's Connected System;
    - a **Connection**: **Has no** `user` in this forest's Connected System, so a person with an account here gets no contact as well;
    - **Legacy Exchange DN** *has a value*, so a contact appears only once its person is a recipient whose identity JIM has imported.

    A connection criterion is decided by the Object Type the person is joined to, never by how the join was made, so the contacts this rule creates can never change its own answer. See [Scoping by connection](../configuration/scoping-criteria.md#scoping-by-connection).

5. **Flow the contact's attributes.** Add these Attribute Flows, with your own container in the distinguished name:

    | Target | Expression |
    |---|---|
    | `distinguishedName` | `"CN=" + EscapeDN(mv["Display Name"]) + ",OU=GALSync,OU=Corp,DC=forestb,DC=local"` |
    | `displayName` | `mv["Display Name"]` |
    | `proxyAddresses` | The expression below |
    | Exchange Recipient Type | `"MailContact"` |
    | Exchange External Address | `mv["Email"]` |
    | Exchange Alias | `Lower(mv["First Name"] + mv["Last Name"])` |

    ```csharp
    Split(Join(mv["Proxy Addresses"], "|") + IIF(HasValue(mv["Legacy Exchange DN"]), "|X500:" + mv["Legacy Exchange DN"], ""), "|")
    ```

    The `proxyAddresses` expression keeps every address the person has and adds an `X500:` address made from their Legacy Exchange DN; an address they already hold is kept once. Add whatever else the address book should show, such as `company`, `department` and `telephoneNumber`, as ordinary Attribute Flows.

6. **Keep the contacts from coming back as people.** If this forest imports its own contacts, scope that import Synchronisation Rule away from the contact container: add the criterion `distinguishedName` *does not contain* `,OU=GALSync,`. The contacts JIM creates stay joined to the export rule that made them, and an import rule's scope decides only the joins that import made, so the forest's own contacts are imported and JIM's are not projected as new people.

7. **For mail-enabled groups,** repeat steps 3 to 5 with your groups' Metaverse Object Type, connections to `group` rather than `user`, and an alias such as `mv["Account Name"]`. Only a mail-enabled group has a Legacy Exchange DN, so the third criterion keeps every other group out.

8. **Run the cycle.** On both Connected Systems, run a Full Import and a Full Synchronisation, then an Export, then an import again so JIM reads back what Exchange stamped on each contact.

## :material-check-decagram-outline: Check it worked

- The contact container in Forest B holds a contact for each mail recipient from Forest A, and the reverse.
- Each contact's Connected System Object shows the **Exchange Recipient** panel with `MailContact` both declared and in Exchange, and nothing outstanding.
- Each contact's `proxyAddresses` include an `X500:` address equal to its source's `legacyExchangeDN`.
- **Administration > Operations**, on the **Exchange** tab, lists no change waiting for a person.
- The import after the export projects no new Metaverse Objects from either forest's contacts: the Activity's counts show no projections for objects in the contact container.

In Exchange, each contact appears in the other forest's address book. Contacts JIM creates are joined and kept up to date: a change to a person's name in their own forest reaches their contact on the next cycle, and a person who leaves has their contact deleted.

If a contact is missing, open its person's Metaverse Object and read the export rule on its Connections tab, which names the criterion the person fails; a person who is not yet a recipient in their own forest has no Legacy Exchange DN. A change that reached Exchange and failed is on the **Exchange** tab, with what to do in [LDAP Connector Exchange Recipients](jim-ldap-connector-exchange.md#troubleshooting).

## :material-arrow-right-circle-outline: Next steps

- [LDAP Connector Exchange Recipients](jim-ldap-connector-exchange.md): how each change reaches Exchange, and what to do when one fails.
- [Scoping Criteria](../configuration/scoping-criteria.md): every criterion an export rule can use.
- [Deprovisioning](../configuration/deprovisioning.md): what happens to a contact when its person leaves.
