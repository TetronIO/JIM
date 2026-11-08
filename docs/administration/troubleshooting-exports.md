---
title: Troubleshooting Exports
description: What each export error means, from a Connected System refusing a change to one it never confirmed, how to fix and check it.
type: troubleshooting
audience: operator
---

# Troubleshooting Exports

An Export run sends each Pending Export to its Connected System. A change the Connected System refuses, or never confirms, stays a Pending Export with its error recorded, and is attempted again at every Export run until the cause is fixed. Open the run's Activity from **Activity** in the portal, or with `Get-JIMActivity`; each failed object's [execution item](../configuration/activity-execution-items.md) names its error, which is the title of one of the entries below. For errors a synchronisation records, see [Troubleshooting Synchronisation](troubleshooting-synchronisation.md).

```jim-diagram
archetype: decision
title: Which export error, by what happened to the change
desc: "Read the failed object's execution item. If JIM never sent the change, because it would place the object outside the selected containers, the error is Export Refused. If the change did not reach a Connected System that could consider it (the connection dropped, or the server was busy or unavailable), it is Operation Failed. If the Connected System refused it because JIM's account may not make the change, it is Access Denied; for any other reason of its own, Export Rejected. If it was accepted but the confirming import read back a different value, it is Export Pending."
start: {label: The execution item}
questions:
- {id: sent, label: 'Did JIM send it?'}
- {id: reached, label: 'Did it reach the system?'}
- {id: accepted, label: 'Did the system accept it?'}
- {id: why, label: 'Why was it refused?'}
outcomes:
- {id: refused, label: Export Refused, sub: outside the selected containers}
- {id: failed, label: Operation Failed, sub: 'connection, busy or unavailable'}
- {id: denied, label: Access Denied, sub: "JIM's account may not make it"}
- {id: rejected, label: Export Rejected, sub: "the system's own reason"}
- {id: pending, label: Export Pending, sub: not confirmed by the next import}
answers:
- {from: sent, to: refused, label: 'no'}
- {from: sent, to: reached, label: 'yes'}
- {from: reached, to: failed, label: 'no'}
- {from: reached, to: accepted, label: 'yes'}
- {from: accepted, to: why, label: 'no'}
- {from: accepted, to: pending, label: 'yes, but not confirmed'}
- {from: why, to: denied, label: permission or credentials}
- {from: why, to: rejected, label: anything else}
```

## :material-account-lock-outline: **Access Denied**: `Export failed: The directory refused the change because the account JIM connects as does not have permission ...`

The SCIM Connector's messages begin `The service provider did not accept JIM's credentials (401)` or `... do not have permission to make it (403)`, and the SQL Connector's begin `The database refused the change because the account JIM connects as does not have permission ...`.

**What it means.** The Connected System refused the export because the account JIM connects as may not make that change, or because it did not accept JIM's credentials at all. The message ends with the Connected System's own words. This is configuration in the Connected System, not a fault in JIM, so the run ends **Complete with warning**. The change waits as a Pending Export, which JIM attempts and reports again at every Export run until it succeeds; the Pending Exports list shows how long it has been failing.

**How to fix.** For a directory, grant the bind account the rights to create, modify, move or delete the object and write the attributes the export carries; the [LDAP Connector](../connectors/jim-ldap-connector.md) page lists what each directory needs. For a SCIM service provider, a 401 means the Connected System's credentials are wrong, expired or revoked; a 403 means they lack a permission or scope the change needs. For a database, grant the account JIM connects as `INSERT`, `UPDATE` and `DELETE` on each table the export writes to, including related tables; [Database account permissions](../connectors/jim-sql-connector-security.md#database-account-permissions) lists what each database needs.

**Check.** The next Export run writes the change, however many attempts failed before: its Pending Export shows **Exported**, and clears once an import confirms it. Nothing needs resetting by hand.

## :material-export: **Export Rejected**: `Export failed: The directory refused the change (...): ...`

The SQL Connector's messages begin `The database refused the change (...)`, naming the SQLSTATE, SQL Server error number or `ORA-` code in brackets.

**What it means.** The Connected System considered the export and said no, for a reason of its own: the object breaks its schema or a constraint, a required value is missing, the parent container is not there, a value does not fit its column, a table or column it names does not exist, a database trigger raised an error, or it will not perform the change. The result in brackets and the rest of the message are the Connected System's own; a SCIM service provider's message is its own throughout. The run ends **Complete with warning**, and the Pending Export is retried as for **Access Denied** above.

**How to fix.** Act on the Connected System's reason: supply the value, correct the data in the source, or change the Attribute Flow that produced it. A **Value Already in Use** error instead means the value is held by another object; [When a target rejects a value](../configuration/generated-values-in-target-systems.md#when-a-target-rejects-a-value) covers that one. A **Password Required** error from Active Directory is explained in [Active Directory will not enable an account that has no password](../connectors/jim-ldap-connector-troubleshooting.md#active-directory-will-not-enable-an-account-that-has-no-password).

**Check.** As for **Access Denied** above.

## :material-export: **Export Refused**: `Export failed: Cannot write to '...': it is outside the containers selected for this Connected System ...`

**What it means.** JIM did not send the export, because it would have placed the object in a container that is not selected on the Connected System's **Scope** tab. JIM cannot import an object from there, so it would treat the object as deleted at the next Full Import. The object stays where it is, and the run ends **Complete with warning**.

**How to fix.** Select the container on the Connected System's **Scope** tab, or change the Attribute Flow that produced the Distinguished Name the message names.

**Check.** As for **Access Denied** above.

## :material-alert-octagon-outline: **Operation Failed**: `Export failed: ...`

**What it means.** The export did not reach a Connected System that could consider it: the connection dropped, the server was busy or unavailable or ran out of time, a database deadlocked, or a SCIM service provider answered with a server error (5xx). JIM records these as errors it did not anticipate, so the run ends **Complete with error**. The Pending Export is retried as for **Access Denied** above.

**How to fix.** Check that the Connected System is reachable and healthy, then run the Export Run Profile again. If the message names something else, collect what **Still stuck?** below lists.

**Check.** As for **Access Denied** above.

## :material-clock-alert-outline: **Export Pending**: `We exported a change, but did not get confirmation of it`

**What it means.** JIM exported a change, but the confirming import read back a different value, or none. The Connected System changed the value as it stored it (often its case), or the import does not return that attribute. JIM exports the change again at every Export run, and reports it on every confirming import that does not see it.

**How to fix.** Compare what JIM exported with what the Connected System holds. Where the system changes the case, see [When a target changes the case of a value](../concepts/case-sensitivity.md#when-a-target-changes-the-case-of-a-value); where the import cannot see the attribute, select it on the Connected System's schema.

**Check.** The next confirming import records no Export Pending items, and the Pending Export clears.

## :material-lifebuoy: Still stuck?

Collect these before asking for help:

- **The Activity's ID** and its status and message, from its page or `Get-JIMActivity -Id <id>`.
- **One failing execution item's error type and message:** `Get-JIMActivityExecutionItem -Id <id>`.
- **The object's Pending Export,** from the Connected System Object's page or `Get-JIMPendingExport`, with how many attempts it has had.
- **The JIM version,** shown at the foot of every portal page and by `Connect-JIM`.
- **The Worker's log from the time of the run:** **Logs** in the portal, or `docker logs jim.worker` (`sudo podman logs jim-worker` on Podman).
