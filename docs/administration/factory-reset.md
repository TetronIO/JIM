---
title: Factory Reset
description: "Wipe JIM's data and configuration back to a fresh installation: what goes, what survives, and how to keep administrative access."
type: how-to
audience: administrator
---

# Factory Reset

A Factory Reset returns JIM to the state of a fresh installation: every Connected System, Synchronisation Rule, Metaverse Object and Activity goes, and the built-in configuration comes back as it shipped. Use it to clear a test or demonstration system, such as one filled with [Example Data](../configuration/example-data.md), or to hand a system over clean. It cannot be undone, so never run it on a system you depend on without a backup.

```jim-diagram
archetype: flow
title: What a Factory Reset does, in order
desc: "A Factory Reset is refused while any Activity is in progress. Otherwise JIM wipes the data in a single database transaction, so a failure removes nothing. It then restores the built-in configuration as it shipped, records a Factory reset Activity naming who ran it and how much it removed, and signs everyone out, including you. Only a backup taken beforehand can undo it."
steps:
- {id: check, label: 'Nothing in progress?', sub: no running Activity, note: 'otherwise refused'}
- {id: wipe, label: Wipe, sub: one database transaction, note: 'a failure removes nothing'}
- {id: restore, label: Built-ins restored, sub: as shipped}
- {id: record, label: Factory reset Activity, sub: 'who ran it, how much'}
- {id: signout, label: Everyone signed out, sub: you included}
```

## :material-delete-sweep-outline: What is removed and what survives

| Removed | Survives |
|---|---|
| Every Connected System, its Connector Space and its Pending Exports | The database schema |
| Every Synchronisation Rule, Run Profile and Schedule | The built-in Metaverse Object Types and attributes, Roles, Connectors, Predefined Searches, Example Data Sets and Example Data Template, and the built-in Schedules, all restored as shipped |
| Every Metaverse Object, and all change history | Service Settings |
| Every Activity and queued Worker Task | The infrastructure API key |
| Custom Object Types, attributes, Roles, Predefined Searches and Example Data | Administrators, unless you choose to remove them |
| Every other API key, and every trusted certificate | The encryption keys, which are files, not database rows |

A few details:

- Administrators who survive keep their accounts and Roles, but lose any value that pointed at removed data (custom attributes, references and where each value came from).
- If the Service Setting that maps a sign-in to a person names a custom attribute, it is cleared; set it again before anyone else signs in.
- Files in the Connector files directory (`/connector-files`) are not touched.

The whole wipe is a single database transaction: if any part of it fails, nothing is removed.

## :material-clipboard-list-outline: Before you begin

- **Take a backup** of the database and the encryption keys, together; see [Backup & Disaster Recovery](backup-recovery.md). It is the only way to undo a reset.
- **Let running work finish.** JIM refuses to reset while any Activity is in progress, and says how many; wait for them, or cancel them from [Operations](../configuration/operations.md), and try again.
- **Know how you will sign back in** if you will remove administrators (step 3).

## :material-restore: Reset JIM

1. Open Administration, select **Danger Zone** at the foot of the page to reveal it, then select **Factory Reset**.
2. Read the lists of what is removed and preserved.
3. To remove administrators as well, tick **Also delete administrator users**.

    !!! danger "You can lock yourself out"
        With administrators removed, only two ways back in remain. One is the initial administrator (`JIM_SSO_INITIAL_ADMIN`), recreated when that person next signs in through single sign-on, which works only if it is configured; see [SSO / Authentication](configuration.md#sso-authentication). The other is the [infrastructure API key](configuration.md#infrastructure-api-key), which works for 24 hours, and which restarting JIM renews once it has expired, provided `JIM_INFRASTRUCTURE_API_KEY` is still set. With neither, nobody can administer JIM. Everything else survives as the table above lists.

    Then tick **I understand the administrator lockout risk and want to proceed**.

4. Type `RESET` in **Type RESET to confirm**, in capitals, and select **Factory Reset**.
5. Read the confirmation, which says whether administrators will be removed, and select **Wipe everything**.

JIM wipes the data, restores the built-in configuration and signs everyone out, you included.

From a script, `Reset-JIMSystem` does the same; see [System](../powershell/system.md#reset-jimsystem). It asks for confirmation unless given `-Force`, and removes administrators only with `-IncludeAdministrators`. The REST API runs it from `POST /api/v1/system/reset`.

## :material-check-decagram-outline: Check it worked

- Sign in again. With administrators kept, your account works as before.
- The Activity list holds a single **Factory reset** Activity, naming who ran it and how much it removed.
- Administration shows no Connected Systems, and the Metaverse holds no objects other than surviving administrators.

## :material-book-open-variant-outline: Related

- [Backup & Disaster Recovery](backup-recovery.md): the backup that makes a reset recoverable
- [Example Data](../configuration/example-data.md): the generated objects only a reset removes
- [Configuration Reference](configuration.md): the settings a reset keeps
