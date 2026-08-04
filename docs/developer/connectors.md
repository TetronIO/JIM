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
| `IConnectorPhases` | Declaring the steps your work goes through, before it starts | The Activity's step list, so an administrator sees what is still to come |
| `IConnectorProgress` | Moving between those steps, and narrating one while it runs | The Activity's stepper and message |
| `ConnectedSystemExportResult.Failed(...)` | One Pending Export failed; the rest are fine | One Run Profile Execution Item per failed object |
| `ConnectedSystemImportObject.ErrorType` | One imported object has a problem; the rest are fine | One Run Profile Execution Item per flagged object |
| `ConnectedSystemImportResult.WarningMessage` | The run succeeded but the administrator should know something | A warning on the Activity |
| Throwing an exception | The run cannot produce trustworthy data | The Activity fails, carrying your message |
| The supplied `ILogger` | Diagnostics for whoever reads the logs | JIM's log output, never the portal |

### Validating settings

`IConnectorSettings.ValidateSettingValues` runs when an administrator saves or tests a Connected System's settings. Return one `ConnectorSettingValueValidationResult` per problem, with `IsValid = false` and an `ErrorMessage` that says what is wrong **and how to fix it**. Attach the offending `SettingValue` where the problem belongs to one setting, and leave it null where the problem is a combination of settings.

This is the cheapest feedback in the whole system: it costs the administrator seconds at configuration time instead of a failed run later. Validate as much as you reasonably can here, including a live connection attempt if your Connected System supports one.

### Declaring the steps of your work

A Connector owns work JIM cannot see inside: loading a file before merging changes into it, asking a directory what has changed, fetching page after page from a container. Left undeclared, that time reads as one long unexplained pause in the middle of a run.

Implement `IConnectorPhases` to say up-front what your work goes through. JIM reads it once, before the run starts, and shows your steps inside the JIM step that calls you:

```csharp
public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
{
    return runProfile.RunType switch
    {
        ConnectedSystemRunType.Export =>
        [
            new ConnectorPhase("load-existing-file", "Loading existing export file"),
            new ConnectorPhase("merge", "Merging changes into file"),
            new ConnectorPhase("write", "Writing the output file")
        ],
        ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport =>
        [
            new ConnectorPhase("read", "Reading the file")
        ],
        _ => []
    };
}
```

Declaring up-front is what makes the steps you have not reached yet visible; steps discovered as they happen can only ever show where you are, never how much is left.

Rules worth following:

- **Declare the steps you can perform, in the order they would occur.**<br /> A step this run turns out not to need is recorded as skipped, not left looking like work still to come, so there is no need to predict the run exactly.
- **A step is a phase of work, not a progress tick.**<br /> JIM's File Connector declares one step for an import, because reading and parsing are one pass over the file; declaring "read" then "parse" would be a fiction. Its export declares three, because loading, merging and writing genuinely happen in turn.
- **Keys are internal and permanent; names are what people read.**<br /> Keys are stored against historic Activities, so renaming one orphans the runs that used it. Improve the name instead.
- **Return an empty list for run types you do not act in.**<br /> Synchronisation never calls a Connector, so a step declared there could never be entered.
- **Keep it cheap and deterministic.**<br /> It is called before the run, so no calls to the Connected System. The list may vary with the Connected System's configuration; it must not vary between two calls with the same configuration.
- **Declaring nothing is a valid answer.**<br /> JIM's LDAP Connector declares no export steps: its export iterates per object, and JIM already reports accurate per-item counts, so a step would say less than the counts do.

A `ConnectorPhaseConformanceTests` base class in the test suite enforces these rules; derive from it in your Connector's tests and supply an instance.

### Narrating progress during a run

All four interaction interfaces (`IConnectorImportUsingCalls`, `IConnectorImportUsingFiles`, `IConnectorExportUsingCalls`, `IConnectorExportUsingFiles`) are handed an `IConnectorProgress`. It is never null, so there is nothing to check before using it:

```csharp
public async Task<ConnectedSystemImportResult> ImportAsync(
    ConnectedSystem connectedSystem,
    ConnectedSystemRunProfile runProfile,
    ILogger logger,
    CancellationToken cancellationToken,
    IConnectorProgress progress)
{
    await progress.EnterPhaseAsync("read");

    // ... and as the read goes on
    await progress.ReportAsync($"Reading the {region} region...");
}
```

`EnterPhaseAsync` moves to one of the steps you declared: the stepper advances, and the step's own name is shown unless you supply a message. `ReportAsync` narrates within the step already running, for detail the step's name cannot carry.

Rules worth following:

- **Emit on phase and page boundaries, never per object.**<br /> Each emit writes to the Activity. A phase that is naturally repetitive should pace itself: JIM's File Connector reports every 10,000 rows read, and its LDAP Connector reports once per page fetched.
- **The vocabulary is yours.**<br /> JIM owns the orchestration phase and the counts and does not interpret your message. Say what you are doing in the administrator's language, not your internal one: "Loading existing export file..." rather than "LoadExistingFileContent".
- **Say what the counts cannot.**<br /> How many objects have arrived, how fast, and how long is left are all shown from the counts below; a message that repeats them says the same thing twice. "Fetching User objects from Employees (page 3)..." earns its place, "Parsed 50,000 rows..." does not.
- **Entering a step you did not declare still works.**<br /> It is appended to the stepper rather than dropped, so nothing you narrate is lost; it just cannot be shown in advance.
- **Do not depend on it succeeding.**<br /> JIM serialises the emits (safe to call from parallel internal work) and swallows any failure to record one, because narration must never fail a synchronisation run. Blank messages are ignored rather than clearing the Activity message.

### Reporting how many objects there are

**Report an object count wherever your Connected System can be asked for one cheaply.** It is the difference between an administrator seeing how far through a long import a run is and seeing a bar with no end to it, and only your Connector is in a position to know.

Two figures, reported independently, through the same `IConnectorProgress`:

```csharp
// The whole run's total, as soon as you know it. Counting a file's records before parsing
// them, or reading the count a query's response states, is worth the extra pass.
await progress.ReportExpectedObjectCountAsync(recordCount);

// How many objects you have read so far within the call you are currently serving.
await progress.ReportObjectsReadAsync(rowsRead);
```

- **`ReportExpectedObjectCountAsync`** gives the fetching step a percentage and a time remaining. Report the whole run's expected total, not the current page's. It is your best answer rather than a guarantee: report it again to correct it, and if more objects turn up than you expected, JIM raises the total rather than letting the bar read past complete. Say nothing if answering would mean doing your own work twice; JIM shows the count and rate alone rather than inventing a figure.
- **`ReportObjectsReadAsync`** moves the counters while your call is still running. JIM cannot count what you have not returned yet, so a Connector that hands everything over in one call leaves the Activity frozen for the whole read unless it reports this. Count only what the current call has read; JIM adds it to what earlier calls delivered. A Connector that returns a page at a time gains little, because JIM counts each page as it arrives.

Between them these are what tell an administrator the difference between a healthy long phase and a stuck run, alongside a step that advances.

Counts are reported on the same terms as messages: serialised, and a failure to record one is swallowed rather than failing the run.

The design behind this, and the vocabulary the built-in Connectors use, is recorded in [`engineering/notes/RUN_PROFILE_PHASES.md`](https://github.com/TetronIO/JIM/blob/main/engineering/notes/RUN_PROFILE_PHASES.md).

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
