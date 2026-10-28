---
title: Configuration Tests
description: "Save inputs and the result an Attribute Flow, Generated Value, Scope or Object Matching Rule should give, and run them to catch a change that breaks it."
type: object
audience: administrator
---

# Configuration Tests

A configuration test case holds inputs, and what part of your configuration should give for them: an Attribute Flow's result, a Generated Value's base, whether an object is in a Synchronisation Rule's Scope, or an Object Matching Rule's match values. Saving a change runs the cases it could affect and stops the save if one of them stops passing, so a change cannot break something that used to work without you deciding it should. A case runs through the same code synchronisation runs, so it gives the result a synchronisation would give for the same inputs. It cannot test what depends on other objects, such as which Generated Value is free or which Metaverse Object a match finds.

```jim-diagram
archetype: decision
title: What a save does when it stops a test case passing
desc: 'Saving a change runs every test case covering it twice: against the configuration as saved, and against the change. If no case that passed before fails with the change, the change is saved; a case that was already failing never stops a save. If one does, nothing is saved yet and you choose. Keep editing, the default, saves nothing and leaves the change on the page to correct. Accept the new result saves the change, and each case that stopped passing now expects what the configuration gives. Save anyway saves the change and leaves those cases failing.'
start: {label: Saving a change}
questions:
- {id: broke, label: 'A passing case now fails?', sub: 'each case runs as saved, and with the change'}
- {id: choose, label: Save stopped, sub: you choose}
outcomes:
- {id: saved, label: Saved, sub: an already failing case never stops it, good: true}
- {id: editing, label: Nothing saved, sub: the change stays on the page}
- {id: accept, label: Saved; cases updated, sub: each expects the new result}
- {id: anyway, label: Saved; cases fail, sub: the cases are left as they are}
answers:
- {from: broke, to: saved, label: 'no'}
- {from: broke, to: choose, label: 'yes'}
- {from: choose, to: editing, label: Keep editing (default)}
- {from: choose, to: accept, label: Accept the new result}
- {from: choose, to: anyway, label: Save anyway}
```

Each case covers exactly one thing: one Attribute Flow, one Synchronisation Rule's Scope, or one Object Matching Rule. It is deleted with what it covers.

## :material-content-save-outline: Save a test case from a tester

The quickest way to make a case is from a test you have just run.

1. Open the tester: the **Try it** pane in an Attribute Flow's dialog, or **Test this scope** on a Synchronisation Rule's Scope tab.
2. Give the inputs values. An Attribute Flow's result follows them as you type; for a scope, choose **Run test**.
3. Choose **Save as test case**. The dialog holds the values you tested as the inputs, and today's result as what the case expects.
4. Give the case a name that says what it checks, change anything you need, and choose **Save**.

**Save as test case** appears once the Attribute Flow or Synchronisation Rule is saved. The case runs against the configuration as saved, so save any edits you were testing before you rely on the case.

## :material-plus-box-outline: Create a test case

1. Open the Synchronisation Rule's **Tests** tab, or for a Connected System in Simple mode, its **Matching** tab.
2. Choose **New test case**, then what it tests: the **Scope**, an Attribute Flow, a Generated Value, or an Object Matching Rule.
3. Give each input the configuration reads a value. Each value is edited as its attribute's type: a date for a date and time attribute, several values for a multi-valued attribute. Tick **No value** for an input that should have none.
4. Choose what the case expects (see [What a case can expect](#what-a-case-can-expect)) and choose **Save**.

## :material-play-circle-outline: Run test cases

On the **Tests** tab (or the **Matching** tab's test cases), choose **Run all**, or the run button on one case. Each result is one of:

| Result | Meaning |
|---|---|
| Passed | The configuration gave what the case expects. |
| Failed | It gave something else. The result says what was expected and what came. |
| Needs review | The case no longer fits the configuration, so it was not run. The configuration now reads an input the case gives no value for, an input's attribute left the schema or changed type, or what the case covers changed shape (an Attribute Flow that now generates a value, for example). Edit the case, not the configuration. |
| Error | The case could not be run. |

Results are worked out each time the cases run and are never stored. Runs test the configuration as saved: an unsaved edit on another tab is not tested until you save it.

From a script, [`Invoke-JIMConfigurationTest`](../powershell/configuration-tests.md#invoke-jimconfigurationtest) runs the cases covering a Synchronisation Rule or under a Connected System, and `-FailOnUnsuccessful` stops a deployment script when one does not pass. The REST API runs them through `POST /api/v1/configuration-tests/run`.

## :material-alert-octagon-outline: When a change stops a case passing

Saving a change to a Synchronisation Rule (its Attribute Flows, Scope or Object Matching Rules), an Attribute Flow or an Object Matching Rule runs every case covering it twice: against the configuration as it was saved, and against the change. A case that passed before and does not with the change stops the save, and nothing is saved. You then choose:

| Choice | What happens |
|---|---|
| **Keep editing** | The default. Nothing is saved, and your change stays on the page to correct. |
| **Accept the new result** | The change is saved, and each case that stopped passing now expects what the configuration gives with it. Only those cases are changed. A case that needs review, could not run, or whose Scope cannot be determined cannot take a new result: edit it, or save anyway. |
| **Save anyway** | The change is saved and the cases are left as they are, so they now fail. |

Accepting or saving anyway is recorded with the change in [Configuration History](configuration-changes.md), naming each case and how it came out, never its values.

A case that was already failing never stops a save, and a change cannot be stopped by a case it did not break. Disabling a Synchronisation Rule, removing an Attribute Flow and deleting either always save; a case covering what you remove is deleted with it. Both runs are as of the same instant, so a case that reads the clock gives the same result in each.

From a script, the cmdlets that save these changes take `-AcceptNewResult` and `-SaveAnyway`; without either, a change that stops a case passing fails, naming each case and what it now gives. The REST API answers such a change with `409 Conflict`, listing each case; send it again with `acceptNewResults` or `saveAnyway` (in the body, or on the query string for a removal) to go ahead.

## :material-pencil-outline: Edit or delete a test case

Choose the edit button on a case to change its name, inputs, run-as-of time or expectation. The editor lists each input the configuration reads now; one the case gives that the configuration no longer reads is shown as **No longer read**, and you can remove it. What a case covers and what it tests cannot change: create another case to test something else.

Deleting a case changes nothing it covered.

## :material-clipboard-text-clock-outline: What a case can expect

| Expectation | For | Passes when |
|---|---|---|
| These values | Attribute Flow, Generated Value, Object Matching Rule | The result is exactly these values. Each is compared as a value of the type the configuration gives, whatever the server's culture or time zone, so `2026-03-02` matches the date 2 March 2026. Text compares exactly, upper and lower case included. Values compare in any order, each repeat counted, unless the case asks for **In this order**. |
| No value | Attribute Flow, Generated Value, Object Matching Rule | The Attribute Flow contributes no value, the Generated Value's base waits for an input, or the Object Matching Rule has no match value. |
| An error | Attribute Flow, Generated Value, Object Matching Rule | The configuration gives an error, of any reason. |
| Any value | Attribute Flow, Generated Value, Object Matching Rule | The configuration gives at least one value, whatever it is. |
| In scope, Out of scope | Scope | The object is in, or not in, the Synchronisation Rule's Scope. A Scope that synchronisation cannot determine (a criterion it cannot evaluate) fails either way. |

A Generated Value case tests the base: the value before the uniqueness token is added. Which value is free is decided when a synchronisation runs. An Object Matching Rule case compares the rule's match values and never looks them up in the Metaverse, so it gives the same result whatever the Metaverse holds.

## :material-calendar-clock-outline: Run as of a fixed time

An expression that reads the clock (`Now()`, `Today()`) or a Scope with a relative date ("leave date is after 30 days ago") gives a result that depends on when it runs. Set **Run as of** on the case to run it as of a fixed date and time (UTC), so it gives the same result every time. **Save as test case** sets it for you when the configuration reads the clock. A case with no run-as-of time whose configuration reads the clock runs as of now, and its result says so.

## :material-account-lock-outline: Personal data in test inputs

Inputs you copy from a real object can hold personal data. Test inputs are stored as configuration, unencrypted, as other configuration is, and the dialog says so before you save. Replace real values with made-up ones before you save a case.

JIM keeps a case's values out of everything else: they are never written to a log or an Activity, and [Configuration History](configuration-changes.md) records only that a case's inputs or expected values changed, never what they were. A case's name is recorded, so keep values out of it. Through the REST API, a list of cases leaves the inputs out unless it asks for `include=inputs`.

## :material-history: Configuration History

Adding, changing or deleting a test case is recorded in the Configuration History of what owns it: the Synchronisation Rule, or the Connected System for a Simple mode Object Matching Rule. These changes are cosmetic: a test case changes nothing that synchronises. When deleting an Attribute Flow, an Object Matching Rule or a Synchronisation Rule deletes the cases covering it, the history entry lists the cases that went with it.

## :material-tune-variant: Limits

A test case is refused when it is larger than these, in the portal, the REST API and PowerShell alike. The message names the limit and, for an input, which one.

| Limit | Most allowed |
|---|---|
| Name | 200 characters |
| Description | 2,000 characters |
| Inputs | 100 per case |
| Values | 100 per input, and 100 expected values |
| Value length | 4,000 characters, for an input value or an expected value |
| Input attribute name | 400 characters |

A Synchronisation Rule holds at most 200 test cases, and so do a Connected System's Simple mode Object Matching Rules between them, because every save runs the cases it could affect. A new case past that is refused.

## :material-book-open-variant-outline: Related

- [Expressions](../concepts/expressions.md#testing-an-expression): testing an Attribute Flow's expression as you write it
- [Scoping Criteria](scoping-criteria.md#testing-a-scope): testing a Scope
- [Object Matching Rules](object-matching-rules.md#testing-a-rule): testing an Object Matching Rule
- [Generated Values](generated-values.md)
- [Configuration Tests cmdlets](../powershell/configuration-tests.md)
