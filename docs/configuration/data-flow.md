# Data Flow

Attribute flows are configured one Synchronisation Rule at a time, which is the right place to change them and the wrong place to understand them. Answering "where does this person's job title actually come from?" means opening every inbound rule for that object type and reading its mappings.

The **Data Flow** view answers it in one place. It lists every attribute data flow configured across all Connected Systems, in both directions, and is read-only by design: rows link to the surface that owns the change rather than editing in place.

Find it at **Administration → Schema → Data Flow**.

## 🔀 What a flow is

One row is one Attribute Flow on one Synchronisation Rule. The direction determines which side of the Metaverse each column refers to:

- **Inbound**<br /> Reads one or more Connected System attributes and writes a single Metaverse Attribute.
- **Outbound**<br /> Reads one or more Metaverse Attributes and writes a single Connected System attribute.

The target is therefore always a single attribute, while the source side may be one attribute, several, or an [expression](../concepts/expressions.md).

## 📊 Reading the columns

| Column | Shows |
|--------|-------|
| **Direction** | Inbound (into the Metaverse) or Outbound (out of it) |
| **Connected System** | The system the rule belongs to, with the Connected System object type beneath it |
| **Object Type** | The Metaverse Object Type the flow applies to |
| **Source** | The attribute or attributes supplying the value; a computed source is shown as **Expression**, with the expression text in its tooltip |
| **Target** | The attribute the flow writes, linked to where it can be inspected |
| **Priority** | The flow's position in its target's [Attribute Priority](../concepts/attribute-priority.md) order. Inbound only |
| **Options** | **Null is a value** on an inbound flow, **Enforce State** on an outbound one |
| **Synchronisation Rule** | The owning rule, linked to its Attribute Flow tab. A disabled rule is marked as such: its flows are still listed, because they remain configuration you are reasoning about, but they move no data |

Priority and "Null is a value" apply to inbound flows only, and Enforce State to outbound flows only, so the cell is blank where the concept does not apply rather than showing a value that would imply it does.

### 🥇 Priority at a glance

An inbound flow's priority is emphasised when its target Metaverse Attribute has **more than one contributor**, because that is the only situation in which the order decides anything. A sole contributor's number is shown quietly: it is a position in a list of one.

A flow that has never been ordered reads as **Unranked**. New inbound Attribute Flows are created that way deliberately, so adding one cannot start winning resolution the moment it is saved; an unranked contribution ranks below every ranked one. Hover the cell for what the number means for that particular flow.

## 🔎 Narrowing the list

Every filter is optional and they combine, so you can go from "everything" to "the one flow I am looking for" in a couple of clicks:

- **Direction**<br /> Inbound or Outbound.
- **Connected System**<br /> Everything one system reads or writes.
- **Metaverse Object Type**<br /> Users, Groups, or any custom type.
- **Metaverse Attribute**<br /> Every flow that touches the attribute, in either direction. An inbound flow matches when it writes the attribute and an outbound flow when it reads it, so this is the fastest way to see an attribute's full journey: which systems feed it, and where it goes afterwards.
- **Contested only**<br /> Just the inbound flows whose target has more than one contributor. These are the flows whose priority order decides which value wins, and the ones worth reviewing after adding a Connected System.
- **Search**<br /> Free text matched against Synchronisation Rules, Connected Systems, object types, attribute names on either side, and expression text.

!!! note "Expressions and the attribute filters"

    An expression's attribute references live in its text and are not modelled, so the **Metaverse Attribute** filter cannot match a flow whose relevant side is an expression. Use **Search** to find those: it looks inside expression text.

## 🔧 Via the REST API and PowerShell

The same view is available for automation, with the same filters:

```powershell
# Every flow that touches the Department attribute, in either direction
Get-JIMDataFlow -MetaverseAttributeId 42

# The inbound flows whose priority order actually decides something
Get-JIMDataFlow -Direction Import -MultipleContributorsOnly

# Everything one Connected System reads or writes
Get-JIMDataFlow -ConnectedSystemName "Contoso AD"
```

Over REST, `GET /api/v1/synchronisation/data-flows` takes the same filters as query-string parameters and pages like every other collection endpoint. See the [API reference](../api/index.md) for the response shape.

## Related

- [Attribute Priority](../concepts/attribute-priority.md): how JIM decides which contribution wins when several systems supply the same Metaverse Attribute.
- [Synchronisation Rules](synchronisation-rules.md): where Attribute Flows are created and edited.
- [Expressions](../concepts/expressions.md): computing a value rather than mapping one attribute to another.
