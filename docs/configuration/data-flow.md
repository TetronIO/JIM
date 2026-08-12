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

The columns are grouped into two bands, one per side of the Metaverse. The **Connected System** band is always on the left and the **Metaverse** band always on the right, whichever way the value travels; only the arrow between them changes. So a row reads as a sentence: this system, this object type, this attribute, flowing into (or out of) this Metaverse Object Type's attribute.

| Band | Column | Shows |
|------|--------|-------|
| Connected System | **System** | The system the rule belongs to |
| Connected System | **Object Type** | The Connected System object type, as the system itself names it |
| Connected System | **Attribute** | The system's attribute: the source inbound, the target outbound. A computed value is shown as an **Ex** chip carrying the expression |
| | **→ / ←** | Which way the value moves. It points right on an inbound flow and left on an outbound one |
| Metaverse | **Object Type** | The Metaverse Object Type the flow applies to |
| Metaverse | **Attribute** | The Metaverse Attribute: the target inbound, the source outbound |
| | **Priority** | The flow's position in its target's [Attribute Priority](../concepts/attribute-priority.md) order, with the number of contributors it is one of. Inbound only |
| | **Null is a value** / **Enforce State** | The setting that applies in the chosen direction. With no direction chosen the column is headed **Options** and carries whichever applies per row |
| | **Synchronisation Rule** | The owning rule, linked to its Attribute Flow tab. A disabled rule is marked as such: its flows are still listed, because they remain configuration you are reasoning about, but they move no data |

Attributes carry a **CS** or **MV** marker naming the side they belong to, reinforcing the bands.

Priority and "Null is a value" apply to inbound flows only, and Enforce State to outbound flows only. Choose a direction and the table drops the column that does not apply rather than showing you a column of blanks.

### 🥇 Priority at a glance

An inbound flow's priority is emphasised when its target Metaverse Attribute has **more than one contributor**, because that is the only situation in which the order decides anything. A sole contributor's number is shown quietly: it is a position in a list of one.

A flow that has never been ordered reads as **Unranked**. New inbound Attribute Flows are created that way deliberately, so adding one cannot start winning resolution the moment it is saved; an unranked contribution ranks below every ranked one. Hover the cell for what the number means for that particular flow.

## 🔎 Narrowing the list

Every filter is optional and they combine, so you can go from "everything" to "the one flow I am looking for" in a couple of clicks:

- **Direction**<br /> Inbound or Outbound.
- **Connected System**<br /> Everything one system reads or writes.
- **Metaverse Object Type**<br /> Users, Groups, or any custom type.
- **Metaverse Attribute**<br /> Every flow that touches the attribute, in either direction. An inbound flow matches when it writes the attribute and an outbound flow when it reads it, so this is the fastest way to see an attribute's full journey: which systems feed it, and where it goes afterwards.
- **Multiple contributors**<br /> Just the inbound flows whose target Metaverse Attribute is fed by more than one Synchronisation Rule. Several systems contributing one attribute is a normal, expected arrangement; this simply narrows the list to where the priority order decides which value wins, which is what is worth reviewing after adding a Connected System.
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
