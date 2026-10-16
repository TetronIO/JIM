---
title: Example Data
description: "Fill the Metaverse with realistic users and groups for a demonstration or a proof of concept, without connecting a single system first."
type: object
audience: evaluator
---

# Example Data

Example Data fills the Metaverse with realistic people and groups, so you can explore JIM, demonstrate it, or try a feature without first connecting systems and waiting for a synchronisation. An **Example Data Template** describes what to generate; **Example Data Sets** supply the names, departments, job titles and other values it draws on. Both are under Administration, then **Example Data**.

!!! warning "Generated objects stay"
    Generated objects are added to the Metaverse permanently. JIM never deletes them automatically, whatever the Object Type's Deletion Rule, and no command removes them one by one; a [Factory Reset](../administration/factory-reset.md) is the only way to clear them. Generate data on a system you can reset, never on one already synchronising real identities.

## Templates

A template names one or more Metaverse Object Types, how many objects of each to create, and how to make each attribute's values. The **Templates** page lists them; opening one shows, for each Object Type, every attribute with its **Population %** and the **Rules** that make its values.

JIM ships one template, **Users & Groups**, which creates 10,000 users and 500 groups:

- **Users** get first and last names, a display name and email address built from them, a sequential Employee ID, a company, department, team, job title and start date, a manager drawn from the users above them, and a status.
- **Groups** get a generated name, a type (security or distribution, with an email address for distribution groups), between 5 and 200 users as members, and up to five owners.

A built-in template cannot be changed or deleted. Build your own with `New-JIMExampleDataTemplate`, or over the REST API; see [Example Data](../powershell/example-data.md#building-a-template-end-to-end) in the PowerShell reference. The portal shows templates but does not create or edit them.

### How values are made

Each attribute's rules decide its values:

| Attribute data type | Rules |
|---|---|
| Text | A value from one or more Example Data Sets; a pattern combining other attributes and sets, such as `{First Name}.{Last Name}@company.com` (`[UniqueInt]` adds a number only where a value would repeat); an [expression](../concepts/expressions.md); or a choice from weighted values |
| Number | Between a minimum and maximum, counting up or chosen at random |
| Date | A random date between a minimum and maximum |
| True or false | Chosen at random |
| Reference | Objects of the chosen Object Types, between a minimum and maximum number for a multi-valued attribute; a Manager attribute builds a reporting hierarchy |
| Identifier | A new identifier for each object |

**Population %** leaves the attribute empty on the rest of the objects, and an attribute can depend on another one's value, such as an email address only for distribution groups.

### Generate the objects

1. Open the template and select **Execute Template**.
2. JIM queues the work for the Worker and records it as a **Data Generation** Activity. The template page shows its progress, then how many objects it created.

Cancelling the Worker Task stops generation, but objects already written stay in the Metaverse.

## Example Data Sets

An Example Data Set is a named list of values that templates draw on. JIM ships thirteen, all in English: **Firstnames Male**, **Firstnames Female**, **Lastnames**, **Companies**, **Departments**, **Teams**, **Job Titles**, **User Statuses**, **Group Statuses**, **Adjectives**, **Colours**, **Words** and **Group Name Endings**. The **Data Sets** page lists them with how many values each holds, and opening one shows its values.

A built-in set cannot be changed or deleted. Add your own, such as your organisation's real department names, with `New-JIMExampleDataSet`.

## Who can use it

Only administrators can open the Example Data pages, and the REST API behind them requires the Administrator role too.

## Related

- [Metaverse](metaverse.md): the Object Types and attributes a template fills
- [Factory Reset](../administration/factory-reset.md): the way to remove generated objects
- [Example Data cmdlets](../powershell/example-data.md): build and run templates from a script
