---
title: Writing Connectors
---

# Writing Custom Connectors

!!! info "Documentation In Progress"
    This page is under active development. The section below on reporting progress and errors is complete;
    the surrounding guide (capability interfaces, registration, testing and packaging) is still to be written.

<!-- TODO: Guide for implementing custom connectors: IConnector interface, capability interfaces (IConnectorImportUsingCalls, IConnectorExportUsingCalls), ConnectorCapabilities, registration, and packaging -->

## Reporting progress and errors

A Connector is the only thing that knows what is happening inside a Connected System. JIM can count the objects you hand back and time how long you took, but everything else (which container you are reading, why one object could not be exported, that a Delta Import quietly became a Full Import) is invisible unless you report it.

Administrators see all of it in one place: the [Activity](../configuration/activities.md) for the Run Profile execution. Choose a channel by **when** the administrator needs to know and **how serious** it is.

| Channel | Use it for | Where it surfaces |
|---------|-----------|-------------------|
| `ValidateSettingValues` | Bad or incomplete configuration, before any run happens | Inline in the portal when settings are saved or tested |
| `progressCallback` | Narrating a long phase while it is running | The Activity message, replacing the previous message |
| `ConnectedSystemExportResult.Failed(...)` | One Pending Export failed; the rest are fine | One Run Profile Execution Item per failed object |
| `ConnectedSystemImportObject.ErrorType` | One imported object has a problem; the rest are fine | One Run Profile Execution Item per flagged object |
| `ConnectedSystemImportResult.WarningMessage` | The run succeeded but the administrator should know something | A warning on the Activity |
| Throwing an exception | The run cannot produce trustworthy data | The Activity fails, carrying your message |
| The supplied `ILogger` | Diagnostics for whoever reads the logs | JIM's log output, never the portal |

### Validating settings

`IConnectorSettings.ValidateSettingValues` runs when an administrator saves or tests a Connected System's settings. Return one `ConnectorSettingValueValidationResult` per problem, with `IsValid = false` and an `ErrorMessage` that says what is wrong **and how to fix it**. Attach the offending `SettingValue` where the problem belongs to one setting, and leave it null where the problem is a combination of settings.

This is the cheapest feedback in the whole system: it costs the administrator seconds at configuration time instead of a failed run later. Validate as much as you reasonably can here, including a live connection attempt if your Connected System supports one.

### Narrating progress during a run

All four interaction interfaces (`IConnectorImportUsingCalls`, `IConnectorImportUsingFiles`, `IConnectorExportUsingCalls`, `IConnectorExportUsingFiles`) accept an optional progress callback:

```csharp
public async Task<ConnectedSystemImportResult> ImportAsync(
    ConnectedSystem connectedSystem,
    ConnectedSystemRunProfile runProfile,
    ILogger logger,
    CancellationToken cancellationToken,
    Func<string, Task>? progressCallback = null)
{
    if (progressCallback != null)
        await progressCallback("Reading CSV file...");

    // ... and on each subsequent phase or page boundary
}
```

JIM's own object counts cannot move while your call is running, because you have not returned any objects yet. A message that keeps changing is the only thing that tells an administrator the difference between a healthy long phase and a stuck run.

Rules worth following:

- **Emit on phase and page boundaries, never per object.**<br /> Each emit writes to the Activity. A phase that is naturally repetitive should pace itself: JIM's File Connector reports every 10,000 rows parsed, and its LDAP Connector reports once per page fetched.
- **The vocabulary is yours.**<br /> JIM owns the phase and the counts and does not interpret your message. Say what you are doing in the administrator's language, not your internal one: "Loading existing export file..." rather than "LoadExistingFileContent".
- **Include scale and identity where you have them.**<br /> "Fetching User objects from Employees (page 3)..." tells an administrator far more than "Fetching...".
- **Skip the work when the callback is null.**<br /> A null callback means nobody is listening; do not build messages for nothing.
- **Do not depend on it succeeding.**<br /> JIM serialises the emits (safe to call from parallel internal work) and swallows any failure to record one, because narration must never fail a synchronisation run. Blank messages are ignored rather than clearing the Activity message.

The design behind this, and the vocabulary the built-in Connectors use, is recorded in [`engineering/notes/CONNECTOR_SUB_PHASE_PROGRESS.md`](https://github.com/TetronIO/JIM/blob/main/engineering/notes/CONNECTOR_SUB_PHASE_PROGRESS.md).

### Reporting a single object that failed to export

`ExportAsync` returns one `ConnectedSystemExportResult` per Pending Export, **in the same order as the Pending Exports you were given**. That positional contract is how JIM attributes an outcome to an object, so never filter or reorder the list.

```csharp
results.Add(ConnectedSystemExportResult.Failed(
    $"The directory rejected the entry: {ex.Message}",
    ConnectedSystemExportErrorType.General));
```

Each failure becomes a Run Profile Execution Item on the Activity, so the administrator can drill from "12 errors" to the twelve objects. `ConnectedSystemExportErrorType` classifies the failure for display and filtering; JIM decides whether to retry from its own attempt count and backoff, not from the type you choose, so classify for the human reading it.

Succeed with feedback too: `ConnectedSystemExportResult.Succeeded(externalId, secondaryExternalId)` is how a system-assigned identifier (an `objectGUID`, an autonumber primary key) gets back into JIM's Connected System Object, so it can confirm the export on the next import.

### Reporting a single object that failed to import

Set `ErrorType` and `ErrorMessage` on the `ConnectedSystemImportObject` and return it in the result as normal. Each flagged object becomes a Run Profile Execution Item carrying your message, and the Activity completes with a warning (or fails, if every object was flagged).

**JIM honours the severity your classification implies**, so choose it deliberately:

| `ConnectedSystemImportObjectError` | Means | What JIM does |
|-----------------------------------|-------|---------------|
| `CouldNotDetermineObjectType` | The object's type is unknown, so nothing can be done with it | Reports it; the object is not imported |
| `ExternalIdAttributes` | The object arrived without the attribute that identifies it | Reports it; the object is not imported |
| `ConfigurationError` | The Connected System's configuration in JIM prevents processing it | Reports it; the object is not imported |
| `AttributeValueError` | One attribute value would not parse; the rest of the object is sound | Reports it, **and imports the object with the values that did parse** |

The last row is the important distinction. An object-level problem means there is nothing importable. An attribute-level problem does not: the object's identity and every other attribute are intact, so withholding it would freeze that identity (name, department, leaver status) over a single malformed value, and for a new joiner would mean never provisioning them at all. Flag the attribute, return the object, and let the administrator fix the source data.

An object you flag still counts as present in the Connected System for deletion detection, provided it carries a usable external ID, so a row that failed to parse never causes its Connected System Object to be deleted as absent.

Do not use this channel for a problem that affects the whole run; throw instead, as below.

### Reporting something the whole run should carry

Set `ConnectedSystemImportResult.WarningMessage` (with an optional `WarningErrorType`) for a non-fatal operational note about **how** the run was performed. The run completes, and the warning is recorded on the Activity.

The canonical example is JIM's LDAP Connector: when a Delta Import finds no usable watermark, it performs a Full Import instead and says so, rather than failing or silently returning a surprising number of objects.

### Failing the run

Throw when the run cannot produce data JIM should trust. JIM fails the Activity and records your exception against it, which is the correct outcome: a partially-imported Connected System that looks complete is far more damaging than a failed run. Prefer the specific exception types in `JIM.Models.Exceptions` (`InvalidSettingValuesException`, `CannotPerformDeltaImportException`, `LdapCommunicationException` and friends) so JIM can present the failure meaningfully.

Never swallow an exception to keep a run alive. If you can genuinely continue, the object-level and warning channels above exist precisely so you can report the problem without hiding it.

### Logging

Use the `ILogger` JIM passes you rather than your own static logger, so your entries carry the run's context. `Verbose` and `Debug` for flow, `Warning` for something unexpected that you handled, `Error` for a failure.

Logs are for whoever reads JIM's log output, not for the administrator watching the portal. Anything an administrator must act on belongs in one of the channels above as well.

Two hard rules:

- **Never log secrets, tokens, credentials or personal data**, sanitised or otherwise.
- **Wrap user-controlled strings with `LogSanitiser.Sanitise()`** (from `JIM.Utilities`) before passing them to a log call, to prevent log injection. Integers, GUIDs, enums and dates are safe as they are.

### Writing good messages

The same message is read by an administrator at 3am during an incident and by a support engineer six months later in a log:

- Say what is happening **now**, in the present tense: "Writing 100,000 rows to output file...".
- Name the thing: the container, the file, the object type, the watermark.
- Include the scale, formatted for humans (`{count:N0}`), so "large and slow" is distinguishable from "small and stuck".
- Say what to do about it in errors. "Group Placeholder Member DN does not reference an existing entry; update it to point to a valid entry in the directory" beats "constraint violation".
- Use British English, and JIM's terms for JIM's concepts (Connected System, Pending Export, Run Profile).
