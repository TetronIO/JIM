---
title: Unresolved References
description: "Why a reference to another object can stay unresolved, how JIM reports it, and how to find what would resolve it."
type: object
audience: administrator
---

# Unresolved References

When an import stages a reference attribute value (for example a group member's Distinguished Name) that does not correspond to any object in the Connector Space, JIM cannot resolve the reference. The most common cause is the referenced object sitting outside the configured [Container Scope](partitions-and-containers.md), which can be entirely deliberate: excluding foreign or out-of-remit objects from import is a normal scoping decision.

```jim-diagram
archetype: comparison
title: The three Unresolved Reference Handling modes
desc: "Error, the default, marks each affected object's Run Profile execution item with an Unresolved Reference error and completes the Activity with a warning showing the errored items; choose it when every reference is expected to resolve. Warn raises no per-object errors and completes the Activity with a warning carrying a summary count and View Affected Objects; choose it when unresolved references are worth a glance but should not read as failures. Ignore raises neither, and the import completes successfully; choose it when unresolved references are expected and benign. In every mode each execution item records how many references were left unresolved, and the same setting governs export."
columns:
  - { label: Error, sub: the default }
  - { label: Warn }
  - { label: Ignore }
rows:
  - label: Execution item
    cells:
      - { text: Unresolved Reference error }
      - { text: no error, sub: references listed in amber }
      - { text: no error, sub: references listed in amber }
  - label: Activity
    cells:
      - { text: warning, sub: showing the errored items }
      - { text: warning, sub: "a summary count, View Affected Objects" }
      - { text: completes successfully }
  - label: Choose it when
    cells:
      - { text: every reference should resolve }
      - { text: "worth a glance, not a failure" }
      - { text: expected and benign }
footer: "In every mode each execution item counts its unresolved references; the setting governs export too"
```

Each Connected System has an **Unresolved Reference Handling** setting that controls what happens when this occurs during import:

| Mode | Behaviour |
|------|-----------|
| **Error** (default) | Each affected object's Run Profile execution item is marked with an Unresolved Reference error, headed **Reference Unresolved**, and the Activity completes with a warning status showing the errored items. The error counts every reference the object was left with, broken down by attribute. Choose this when every reference is expected to resolve. |
| **Warn** | No per-object errors are raised. The Activity completes with a warning carrying a summary of how many references could not be resolved, and a **View Affected Objects** button that opens them in the Connector Space. Choose this when unresolved references are worth a glance but should not read as failures. |
| **Ignore** | No per-object errors and no Activity warning; the import completes successfully. Choose this when unresolved references are expected and benign. |

Whichever mode is selected, each affected object's execution item records how many of its references the import left unresolved, so none is ever lost from the Activity. Under Error, an object whose item already carries a different error (for example an attribute value the Connector could not parse) keeps that error, and its item lists the unresolved references as well, in amber rather than red. Under Warn and Ignore, the item is not marked as an error, but opening it lists the references, in amber.

The same setting also governs **ambiguous references**: a reference value that matches objects of more than one Object Type, where the attribute does not declare which Object Type it points at. Two Object Types may legitimately share an anchor value space (a view over a table has the table's keys by construction), so JIM never resolves such a reference by guessing; it is reported per the mode above, with a message naming the candidate Object Types. Where the Connector's schema can declare the reference's target Object Type (the [SQL Connector](../connectors/jim-sql-connector.md)'s `referencesObjectType`), declaring it removes the ambiguity entirely: the reference resolves within the declared Object Type alone.

Whichever mode is selected, genuine data-quality issues remain discoverable:

- **Connected System page and Connector Space**<br /> While any reference is unresolved, the Connected System page shows an **Unresolved References** button beside **Connector Space** and **Pending Exports**, its count amber under Error and Warn and plain under Ignore. It opens the Connector Space filtered to the affected objects (**Filter by References: Unresolved**), each with how many it holds, beneath **Where they point** (see below).
- **Connected System Objects**<br /> Unresolved reference values stay stored on the affected objects. The object's detail page opens with an **Unresolved References** panel listing every one, with its likely cause, where the fix lies and the next step (see [Finding out why a reference is unresolved](#finding-out-why-a-reference-is-unresolved)), and each unresolved value in the Attributes table carries its likely cause too.
- **Activity execution items**<br /> Each item records how many references its object was left with (`UnresolvedReferenceCount` from `Get-JIMActivityExecutionItem` and the REST API), whatever the mode and whatever error the item carries. Opening the item lists the references as they stand now on the object, says how that compares with what the run left (fewer remain once some resolve), and links to the object.
- **PowerShell**<br /> `Get-JIMConnectedSystemUnresolvedReferenceSummary` summarises a Connected System's unresolved references by likely cause and next step, `Get-JIMConnectedSystemObject -HasUnresolvedReference` lists the objects holding them, and `Get-JIMConnectedSystemObjectUnresolvedReference` lists one object's with their likely causes, or summarises them with `-Summary`.
- **Service log**<br /> Every unresolved reference is logged (at Warning level in Warn mode, Debug level in Ignore mode), along with a summary count at the end of reference resolution.

Set the mode from the **Import Behaviour** panel on the Connected System's Settings tab, with `Set-JIMConnectedSystem -UnresolvedReferenceHandling`, or via the REST API.

## :material-magnify: Finding out why a reference is unresolved

For each unresolved reference, JIM works out the most likely cause by comparing the value with the Connected System's partitions and its current Container Scope, using the Connector's own rule for which Container holds a value (for a directory, the Distinguished Name's position in the hierarchy). It then says whether the fix lies in JIM's configuration or in the Connected System, and what to do:

| Likely cause | What it means | Where to fix it |
|--------------|---------------|-----------------|
| **Container not in scope** | The referenced object sits in a Container that is not selected, so it was never imported. | JIM: select the Container on the Scope tab, then run a Full Import. |
| **Container excluded** | The referenced object sits beneath a Container excluded from Container Scope. | JIM: lift the exclusion on the Scope tab if its objects belong in JIM, then run a Full Import. |
| **Partition not selected** | The referenced object is in a partition that is not selected. | JIM: select the partition on the Scope tab, then run a Full Import. |
| **Ambiguous Object Type** | The value matches objects of more than one Object Type, and the attribute does not declare which one it references. | JIM: declare the attribute's target Object Type on the Schema tab, where the Connector supports it. |
| **Outside this Connected System** | The value is in none of the Connected System's partitions, for example a member from another domain. JIM cannot import the object it names. | The Connected System. If references like this are expected, set Unresolved Reference Handling to Warn. |
| **Not found in scope** | The value sits in Container Scope, but nothing with that value has been imported. | The Connected System. Run a Full Import to rule out a missed change; if it is still unresolved, the object no longer exists there, or its Object Type is not imported. |
| **Not found** | JIM cannot place the value in a Container (the Connector does not say which Container holds a value, as with a file or a database), and nothing with that value has been imported. | The Connected System, as for **Not found in scope**. |

The causes are judged against the Container Scope as it stands now, not as it stood when the import ran, so they always describe what would fix the references today: once you select a Container, its references read **Not found in scope** until the next Full Import imports the objects they name and resolves them. Above the list, the panel's **Next steps** group the references by the change that would fix them, most first, so a group whose hundreds of unresolved members all sit in one unselected Container shows one step rather than hundreds. Steps you can take in JIM are listed first, under **Fix in JIM**, then those that lie in the Connected System. **Where they point**, above the filtered Connector Space, gives the same steps for the whole Connected System, each with how many objects its references sit on, so a thousand affected objects read as the two or three changes behind them. Nothing about the references themselves changes until an import resolves them.

## :material-export: On export

The same setting governs the export side. When JIM writes a reference attribute (a manager, a group's members) it needs the referenced object's own identifier in the target Connected System, which it can only have once that object exists there. A Pending Export whose references cannot all be resolved yet is not held back whole: everything that can be written is written now (a Create inserts the row without the reference columns; a group gains the members that can be resolved), and the Pending Export stays pending, carrying only the reference values still owed, until they resolve. It is retried on the deferred cadence and finished when the referenced objects can be addressed.

JIM tells two situations apart at export time:

| Situation | What it means | What happens |
|-----------|---------------|--------------|
| **Awaiting anchor** | The referenced object has a Connected System Object in this Connected System, but its own export has not been executed or confirmed yet, so it has no anchor to point at. | Ordinary ordering. Nothing is reported; the reference is written on a later run. |
| **Not in this Connected System** | The referenced object has no Connected System Object in this Connected System at all: it is out of scope for every Synchronisation Rule into the system, or has not been provisioned. | The reference cannot be written as things stand, and is reported per the mode above: **Error** marks the referring object's Run Profile execution item with an Unresolved Reference error naming the attribute and the referenced object, and the Activity completes with a warning; **Warn** completes the Activity with a warning carrying a summary count; **Ignore** logs only. |

An export that wrote in part is counted as succeeded on the Activity ("43 succeeded (4 written in part, awaiting references)"), because something was written; the Pending Export detail page lists each reference still owed with its reason, and the same detail is available from `Get-JIMPendingExport -Id` and the REST API as `unresolvedReferences`. A partial write that the target refuses (a reference column declared `NOT NULL`, say) fails that object with an ordinary export error, and is retried like any other failure.
