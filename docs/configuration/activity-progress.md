---
title: Live Progress
description: How a running Run Profile reports its progress live in the portal, the REST API and PowerShell, step by step.
type: reference
audience: operator
---

# Live Progress

While a Run Profile executes, its progress is available in real time on every surface:

- **JIM portal**<br /> The Activity detail page updates as the run progresses (pushed over the real-time notification channel, with polling as a fallback): the current phase, a progress bar with the percentage beside it, a labelled readout of objects processed, throughput and time remaining beneath it, and live operation counts (for example, Connected System Objects added, updated and deleted). Each figure is stated once; the message under the steps narrates what is happening rather than repeating the numbers. The readout names the step it measures ("Step 2 of 3: Processing Connected System Objects"), matching what `Get-JIMActivity -Follow` prints.
- **REST API**<br /> `GET /api/v1/activities/{id}/progress` returns a lightweight progress snapshot: status, phase message, object counts, percentage complete, throughput, estimated seconds remaining, and a live operation-type breakdown. It is designed for frequent polling and is much cheaper to serve than the full Activity detail endpoint; stop polling once the status reaches a terminal value.
- **PowerShell**<br /> [`Get-JIMActivity -Follow`](../powershell/activities.md) follows an in-progress Activity's live progress until it completes, and [`Start-JIMRunProfile -Wait`](../powershell/run-profiles.md) displays the same live progress while blocking until completion.

**Every live figure describes the step running now, not the whole run.** The objects processed, the percentage, the throughput and the time remaining are all reset by each step that counts its own work, which is why the readout names its step. There is no whole-run estimate: steps differ too much in cost, and several cannot know their totals in advance, so any run-level figure would be invented rather than measured. How far through the run you are is answered by the steps themselves.

Throughput and the estimated time remaining are derived from recent progress samples, so they reflect the current phase of the run rather than a whole-run average; they appear once enough samples exist and adapt as the run moves between phases. Where a step cannot know its total in advance, the progress bar runs indeterminate and the readout reports how many objects have been processed so far, without a percentage or a time remaining. Whether an import knows depends on the Connected System: a file's records are counted before they are read, so a file import shows a percentage throughout, while a directory cannot be asked how many objects a search will return without running it twice, so those imports show the count and the rate without a percentage rather than a figure JIM guessed. When the counter reaches its total but the step is still finishing its work, the time remaining reads "Finishing up" rather than counting down to a moment that has already passed.

## The steps of a run

A Run Profile execution is a journey through several steps, and the Activity shows all of them: what is done, what is running now, and what is still to come. An import, for example, connects to the Connected System, imports objects (fetching them, then matching them against the Connected System Objects already held), processes deletions, resolves references, saves changes, reconciles Pending Exports and records its results. An export prepares, writes the changes, makes a second pass over anything it had to defer, resolves change history references, selects any containers it created, and delivers initial passwords to the accounts it provisioned. A Synchronisation prepares, processes Connected System Objects, resolves cross-page references and reviews export scope.

- **Completed steps** carry how long they took, so a run that took four hours can be read afterwards to see where the four hours went.
- **The step running now** is highlighted. The steps inside it and the message describing what it is doing appear beneath, under the line naming the step and above the figures measuring it, so each reads after the thing it belongs to.
- **Steps still to come** are greyed, so "how much is left" is answerable at a glance.
- **A green ring with a dash** marks a step that was not needed on this run: a Delta Import performs no deletion detection, for example. It is green because the run is past it; it is a ring rather than a filled tick because it did no work of its own. Hovering the step says so. This is normal, not a problem. Work a run could never do at all (a file-based import opens no connection) is not shown as a step.
- **A failed run** marks the step it failed in, which is where to look first.

The progress bar beneath the steps counts the work of the step currently running, and the leg of the rail leaving that step fills to match, so the same progress reads at a glance and in exact numbers. Most steps count objects. A Synchronisation's **Resolving cross-page references** step counts reference values instead, because its work grows with them: a group of 750,000 members is that much more work than a group of three. Its message gives the number of objects. Several steps count their own work, so the bar restarts as the run moves between them: that is the step advancing, not progress being lost.

## Connector steps and messages

Some of a run's wall-clock time is spent inside the Connector, on work JIM cannot count objects for: reading an export file before merging changes into it, writing the merged file back out, querying a directory's root DSE, or fetching a page of objects from a container. Connectors declare these as their own steps, shown inside the step that called them, and narrate what they are doing as they go:

- **File connector**<br /> "Loading existing export file", "Merging changes into file" and "Writing the output file" during an export; "Reading the file" during an import, with the records counted before they are read so the bar has a total from the start.
- **LDAP connector**<br /> "Querying the directory" and "Fetching objects" during a Full Import, with messages naming the container and page ("Fetching User objects from Employees (page 3)..."); a Delta Import adds "Querying changes" and "Querying deleted objects", with the watermark in the message ("Querying changes since USN 1,204,933...").

A Connector can report how many objects it has read while it is still working, and both built-in Connectors do, so the counts move during these steps rather than standing still until the Connector returns. A Connector that reports nothing leaves the steps and messages to do that job on their own: something that keeps changing is how you tell a healthy long-running phase from a stuck one. A Connector's steps finish the moment its call returns, so nothing is shown as running once JIM has taken over.

The steps are also available to automation: the Activity progress endpoint reports the current step and its position in the run, and `Start-JIMRunProfile -Wait` and `Get-JIMActivity -Follow` display it as "Step 3 of 7: Saving changes".

## The run's steps in the queue

**Admin > Operations > Queue** shows the same run in miniature, so an administrator watching several at once does not have to open each in turn. Each row's Progress cell carries two things, one above the other:

- **The run's steps**, as a row of segments, one per step, coloured by what happened to each: done, running, skipped, failed, or not reached yet.
- **The running step's own progress bar**, with a caption naming the step the figures belong to ("Step 3 of 7: Saving changes - 12,480 / 40,000").

The two measure different things on purpose: the segments say *where* in the run you are, the bar says *how much* of the step is done. The caption is what keeps them apart, which is why it names the step rather than just counting.

A task that is not a Run Profile execution records no steps (clearing Connected System Objects, generating example data, a factory reset), and shows the bar and its message alone, as it always has.

The full picture, with step names, durations and the messages beneath them, is on the Activity itself; the row's name links straight to it.
