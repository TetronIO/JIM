---
title: Configuration Tests
description: "Save inputs and the result an Attribute Flow, Generated Value, Scope or Object Matching Rule should give, and run them to catch a change that breaks it."
type: object
audience: administrator
---

# Configuration Tests

A configuration test case holds inputs, and what part of your configuration should give for them: an Attribute Flow's result, a Generated Value's base, whether an object is in a Synchronisation Rule's Scope, or an Object Matching Rule's match values. Running the cases after a change shows at once whether the change broke something that used to work, before a synchronisation runs on real objects. A case runs through the same code synchronisation runs, so it gives the result a synchronisation would give for the same inputs. It cannot test what depends on other objects, such as which Generated Value is free or which Metaverse Object a match finds.

Each case covers exactly one thing: one Attribute Flow, one Synchronisation Rule's Scope, or one Object Matching Rule. It is deleted with what it covers.

## Save a test case from a tester

The quickest way to make a case is from a test you have just run.

1. Open the tester: **Test this Attribute Flow** in an Attribute Flow's dialog, or **Test this scope** on a Synchronisation Rule's Scope tab.
2. Give the inputs values and choose **Run test**.
3. Choose **Save as test case**. The dialog holds the values you tested as the inputs, and today's result as what the case expects.
4. Give the case a name that says what it checks, change anything you need, and choose **Save**.

**Save as test case** appears once the Attribute Flow or Synchronisation Rule is saved. The case runs against the configuration as saved, so save any edits you were testing before you rely on the case.

## Create a test case

1. Open the Synchronisation Rule's **Tests** tab, or for a Connected System in Simple mode, its **Matching** tab.
2. Choose **New test case**, then what it tests: the **Scope**, an Attribute Flow, a Generated Value, or an Object Matching Rule.
3. Give each input the configuration reads a value. Each value is edited as its attribute's type: a date for a date and time attribute, several values for a multi-valued attribute. Tick **No value** for an input that should have none.
4. Choose what the case expects (see [What a case can expect](#what-a-case-can-expect)) and choose **Save**.

## Run test cases

On the **Tests** tab (or the **Matching** tab's test cases), choose **Run all**, or the run button on one case. Each result is one of:

| Result | Meaning |
|---|---|
| Passed | The configuration gave what the case expects. |
| Failed | It gave something else. The result says what was expected and what came. |
| Needs review | The case no longer fits the configuration, so it was not run. The configuration now reads an input the case gives no value for, an input's attribute left the schema or changed type, or what the case covers changed shape (an Attribute Flow that now generates a value, for example). Edit the case, not the configuration. |
| Error | The case could not be run. |

Results are worked out each time the cases run and are never stored. Runs test the configuration as saved: an unsaved edit on another tab is not tested until you save it.

From a script, [`Invoke-JIMConfigurationTest`](../powershell/configuration-tests.md#invoke-jimconfigurationtest) runs the cases covering a Synchronisation Rule or under a Connected System, and `-FailOnUnsuccessful` stops a deployment script when one does not pass. The REST API runs them through `POST /api/v1/configuration-tests/run`.

## Edit or delete a test case

Choose the edit button on a case to change its name, inputs, run-as-of time or expectation. The editor lists each input the configuration reads now; one the case gives that the configuration no longer reads is shown as **No longer read**, and you can remove it. What a case covers and what it tests cannot change: create another case to test something else.

Deleting a case changes nothing it covered.

## What a case can expect

| Expectation | For | Passes when |
|---|---|---|
| These values | Attribute Flow, Generated Value, Object Matching Rule | The result is exactly these values. Each is compared as a value of the type the configuration gives, whatever the server's culture or time zone, so `2026-03-02` matches the date 2 March 2026. Text compares exactly, upper and lower case included. Values compare in any order, each repeat counted, unless the case asks for **In this order**. |
| No value | Attribute Flow, Generated Value, Object Matching Rule | The Attribute Flow contributes no value, the Generated Value's base waits for an input, or the Object Matching Rule has no match value. |
| An error | Attribute Flow, Generated Value, Object Matching Rule | The configuration gives an error, of any reason. |
| Any value | Attribute Flow, Generated Value, Object Matching Rule | The configuration gives at least one value, whatever it is. |
| In scope, Out of scope | Scope | The object is in, or not in, the Synchronisation Rule's Scope. A Scope that synchronisation cannot determine (a criterion it cannot evaluate) fails either way. |

A Generated Value case tests the base: the value before the uniqueness token is added. Which value is free is decided when a synchronisation runs. An Object Matching Rule case compares the rule's match values and never looks them up in the Metaverse, so it gives the same result whatever the Metaverse holds.

## Run as of a fixed time

An expression that reads the clock (`Now()`, `Today()`) or a Scope with a relative date ("leave date is after 30 days ago") gives a result that depends on when it runs. Set **Run as of** on the case to run it as of a fixed date and time (UTC), so it gives the same result every time. **Save as test case** sets it for you when the configuration reads the clock. A case with no run-as-of time whose configuration reads the clock runs as of now, and its result says so.

## Personal data in test inputs

Inputs you copy from a real object can hold personal data. Test inputs are stored as configuration, unencrypted, as other configuration is, and the dialog says so before you save. Replace real values with made-up ones before you save a case.

JIM keeps a case's values out of everything else: they are never written to a log or an Activity, and [Configuration History](configuration-changes.md) records only that a case's inputs or expected values changed, never what they were. A case's name is recorded, so keep values out of it. Through the REST API, a list of cases leaves the inputs out unless it asks for `include=inputs`.

## Configuration History

Adding, changing or deleting a test case is recorded in the Configuration History of what owns it: the Synchronisation Rule, or the Connected System for a Simple mode Object Matching Rule. These changes are cosmetic: a test case changes nothing that synchronises. When deleting an Attribute Flow, an Object Matching Rule or a Synchronisation Rule deletes the cases covering it, the history entry lists the cases that went with it.

## Related

- [Expressions](../concepts/expressions.md#testing-an-expression): testing an Attribute Flow's expression as you write it
- [Scoping Criteria](scoping-criteria.md#testing-a-scope): testing a Scope
- [Object Matching Rules](object-matching-rules.md#testing-a-rule): testing an Object Matching Rule
- [Generated Values](generated-values.md)
- [Configuration Tests cmdlets](../powershell/configuration-tests.md)
