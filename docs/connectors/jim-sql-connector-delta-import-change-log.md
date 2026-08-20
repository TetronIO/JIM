# Delta Import with a change-log table

This guide sets up a Delta Import for the [JIM SQL Connector](jim-sql-connector.md) in **Change-Log Table** mode, from an empty database to a scheduled run. It is the recommended mode, and the only one that observes a deletion.

## How it works

Your database keeps a table (or a view over one it already keeps) holding one row per change: the changed object's anchor, what kind of change it was, and a value that only ever goes up. On each Delta Import, JIM:

1. Notes the highest sequence value in the change log before reading anything. That is the watermark it will save when the run completes.
2. Reads every row above the watermark it saved last time, in sequence order, a page at a time.
3. Keeps only the last change recorded for each object in the page: an object created and then updated three times is one import, not four, and importing the earlier states would be work the later one immediately undoes.
4. For creates and updates, reads the object **as it now stands** from the Object Type's own table or view, related tables included. The change log says *which* objects changed; the source says what they now hold.
5. For deletions, imports the anchor alone, which is what tells JIM the object has gone.

A create or update whose row has since disappeared produces nothing: the change log holds a deletion for it further on. A row whose change type is a value your configuration does not list is reported as an error against that object, naming the value, so a new code appearing in the log is something you hear about rather than something that is skipped.

!!! note "Rows must become visible in sequence order"
    JIM captures the highest sequence value at the start of the run and never looks below it again. A change-log row is written in the same transaction as the change it records, so a long-running transaction can commit a *lower* sequence value after a higher one has already been read, and that row is then behind the watermark for ever. Keep the transactions that write to synchronised tables short, and schedule a periodic Full Import as the backstop that every Delta Import strategy needs.

## 1. Create the change-log table

One change-log table can serve several Object Types if it carries every Object Type's anchor columns, but one table per Object Type is simpler to reason about and to purge. The table needs:

| Column | Purpose | Notes |
|--------|---------|-------|
| Sequence | Orders the changes and is the watermark. | Any type with a total order that only goes up: an identity or sequence-backed integer is the safest choice; a timestamp works but is subject to clock adjustments and to two changes sharing a value (JIM orders ties by anchor, so nothing is skipped, but a clock stepped backwards can leave rows behind the watermark). |
| Anchor column(s) | Which object changed. | One per anchor column of the Object Type, in the same order, holding the same values. |
| Change type | What happened. | Any values you like; you tell JIM which of them mean a create, an update and a deletion. Matched case-insensitively. |

Anything else in the table (who made the change, when, from which application) is yours to keep and is ignored by JIM.

```sql title="Microsoft SQL Server"
CREATE TABLE HR.IDM_CHANGE_LOG (
    CHANGE_ID    BIGINT IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    EMPLOYEE_ID  INT           NOT NULL,
    CHANGE_TYPE  CHAR(1)       NOT NULL,   -- I, U or D
    CHANGED_AT   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
```

```sql title="Oracle Database"
CREATE TABLE HR.IDM_CHANGE_LOG (
    CHANGE_ID    NUMBER(19) GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    EMPLOYEE_ID  NUMBER(10)  NOT NULL,
    CHANGE_TYPE  CHAR(1)     NOT NULL,     -- I, U or D
    CHANGED_AT   TIMESTAMP   DEFAULT SYSTIMESTAMP NOT NULL
);
```

The primary key on the sequence column doubles as the index JIM's `WHERE CHANGE_ID > :watermark ORDER BY CHANGE_ID` reads through, so no further index is needed for JIM's own query.

!!! tip "An audit table you already have"
    Where the application already records its changes, a **view** that presents that table with the three columns JIM needs is a change log too. Name the view as the `table` in the configuration below. The view must be able to express the deletion rows; an audit trail that records nothing for a delete cannot serve as a change log, and Watermark Column mode plus a periodic Full Import is the alternative.

## 2. Write the changes into it

A trigger on the Object Type's table is the usual way, because it runs inside the same transaction as the change and cannot be forgotten by an application path. Log a change to a **related table** as an update to its parent: JIM re-reads the parent whole, related rows included, so a phone number added or a membership revoked is picked up as a change to the person or the group it belongs to.

```sql title="Microsoft SQL Server"
CREATE TRIGGER HR.TR_EMPLOYEES_IDM_CHANGE_LOG
ON HR.EMPLOYEES
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    -- Inserts and updates: rows in "inserted"; an update also has the row in "deleted".
    INSERT INTO HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    SELECT i.EMPLOYEE_ID, CASE WHEN d.EMPLOYEE_ID IS NULL THEN 'I' ELSE 'U' END
    FROM inserted AS i
    LEFT JOIN deleted AS d ON d.EMPLOYEE_ID = i.EMPLOYEE_ID;

    -- Deletes: rows in "deleted" with no counterpart in "inserted".
    INSERT INTO HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    SELECT d.EMPLOYEE_ID, 'D'
    FROM deleted AS d
    LEFT JOIN inserted AS i ON i.EMPLOYEE_ID = d.EMPLOYEE_ID
    WHERE i.EMPLOYEE_ID IS NULL;
END;
GO

-- A related table's change is a change to its parent.
CREATE TRIGGER HR.TR_EMPLOYEE_PHONES_IDM_CHANGE_LOG
ON HR.EMPLOYEE_PHONES
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    SELECT EMPLOYEE_ID, 'U' FROM inserted
    UNION
    SELECT EMPLOYEE_ID, 'U' FROM deleted;
END;
GO
```

```sql title="Oracle Database"
CREATE OR REPLACE TRIGGER HR.TRG_EMPLOYEES_IDM_CHANGE_LOG
AFTER INSERT OR UPDATE OR DELETE ON HR.EMPLOYEES
FOR EACH ROW
BEGIN
    IF INSERTING THEN
        INSERT INTO HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE) VALUES (:NEW.EMPLOYEE_ID, 'I');
    ELSIF UPDATING THEN
        INSERT INTO HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE) VALUES (:NEW.EMPLOYEE_ID, 'U');
    ELSE
        INSERT INTO HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE) VALUES (:OLD.EMPLOYEE_ID, 'D');
    END IF;
END;
/

-- A related table's change is a change to its parent.
CREATE OR REPLACE TRIGGER HR.TRG_EMPLOYEE_PHONES_IDM_CHANGE_LOG
AFTER INSERT OR UPDATE OR DELETE ON HR.EMPLOYEE_PHONES
FOR EACH ROW
BEGIN
    INSERT INTO HR.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    VALUES (COALESCE(:NEW.EMPLOYEE_ID, :OLD.EMPLOYEE_ID), 'U');
END;
/
```

Where the Object Type reads from a **view**, put the triggers on the underlying table or tables, logging the anchor as the view exposes it.

## 3. Let JIM read it

Grant the JIM account `SELECT` on the change-log table alongside the grants it already has on the Object Type's source. Nothing else: JIM never writes to the change log and never purges it.

```sql title="Microsoft SQL Server"
GRANT SELECT ON HR.IDM_CHANGE_LOG TO jim_sync;
```

```sql title="Oracle Database"
GRANT SELECT ON HR.IDM_CHANGE_LOG TO jim_sync;
```

## 4. Configure the Object Type

Add a `changeLog` to **every Object Type that is selected for synchronisation** in the Connected System's **Object Types** document. If a selected Object Type has no `changeLog` when this mode is chosen, saving is refused and the message names the Object Type; otherwise a Delta Import could report success while that Object Type's changes went unread. An Object Type that is not selected (a table JIM only exports to, say) takes no part in a Delta Import and needs no change log.

```json title="Object Types with a change log"
{
  "objectTypes": [
    {
      "name": "Person",
      "schema": "HR",
      "table": "V_EMPLOYEES",
      "anchorColumns": [ "EMPLOYEE_ID" ],
      "relatedTables": [
        {
          "attributeName": "PhoneNumbers",
          "schema": "HR",
          "table": "EMPLOYEE_PHONES",
          "valueColumn": "PHONE_NUMBER",
          "joinColumns": [ "EMPLOYEE_ID" ]
        }
      ],
      "changeLog": {
        "schema": "HR",
        "table": "IDM_CHANGE_LOG",
        "anchorColumns": [ "EMPLOYEE_ID" ],
        "sequenceColumn": "CHANGE_ID",
        "changeTypeColumn": "CHANGE_TYPE",
        "createValues": [ "I" ],
        "updateValues": [ "U" ],
        "deleteValues": [ "D" ]
      }
    }
  ]
}
```

| Field | Meaning |
|-------|---------|
| `table` / `schema` | The change-log table or view. |
| `anchorColumns` | The change log's columns carrying the changed object's anchor: one per anchor column of the Object Type, in the same order. |
| `sequenceColumn` | The column that orders the changes. It becomes the watermark. |
| `changeTypeColumn` | The column saying what kind of change a row records. |
| `createValues` / `updateValues` | Your own values meaning "created" and "updated". At least one of the two lists is required. |
| `deleteValues` | Your values meaning "deleted". Required: observing deletions is what a change-log table is for, and a database that cannot record them should use [Watermark Column mode](jim-sql-connector-delta-import-watermark.md) instead. |

A value may appear in only one list, and no value may be blank. Where your application records finer-grained codes (say `INSERT`, `MERGE`, `RESTORE` for things that leave a row present), list them all under `createValues` or `updateValues`; the two are treated the same way, since JIM reads the row as it stands either way.

## 5. Choose the mode and save

On the Connected System's Settings tab, set **Delta Import Mode** to **Change-Log Table** and save. The configuration is checked as you save: a selected Object Type without a `changeLog`, a change-type value listed under two kinds of change, or a missing field is reported immediately. The same check runs when you select an Object Type on the Schema tab (or through the REST API or PowerShell): selecting one that has no `changeLog` while this mode is set is refused there and then, so give it a change log first, or leave it unselected.

## 6. Baseline with a Full Import

Create a **Full Import** Run Profile and a **Delta Import** Run Profile for the Connected System, then run the Full Import once. As well as loading every object, a Full Import records the change log's current high-water mark **before it reads a single row**, so a change made while the Full Import is running is read by the first Delta Import rather than lost between the two.

If you run the Delta Import first instead, JIM performs a Full Import in its place, says so on the Activity as a warning, and establishes the watermark that way; the next Delta Import runs normally.

## 7. Schedule it

Schedule the Delta Import at whatever cadence the application warrants, and a Full Import at a longer interval as the reconciliation backstop. See [Schedules](../configuration/schedules.md).

## 8. Keep the change log short

JIM never deletes from the change log, so schedule a purge of your own. Delete rows by age, keeping them for comfortably longer than the gap between your Delta Imports; thirty days is typical. A row deleted before JIM has read it is a change JIM will not see until the next Full Import.

```sql title="Microsoft SQL Server"
DELETE FROM HR.IDM_CHANGE_LOG WHERE CHANGED_AT < DATEADD(DAY, -30, SYSUTCDATETIME());
```

```sql title="Oracle Database"
DELETE FROM HR.IDM_CHANGE_LOG WHERE CHANGED_AT < SYSTIMESTAMP - INTERVAL '30' DAY;
```

## Troubleshooting

**"No `Delta Import Mode` has been chosen for this Connected System"**<br />
The **Delta Import Mode** setting is blank. Choose **Change-Log Table** (or **Watermark Column**) on the Connected System's Settings tab and save; JIM does not assume a mode, because a Delta Import with no change source would have to read everything every time.

**"A Delta Import was requested, but JIM holds no watermark ... A Full Import was performed instead"**<br />
Expected on the first run and after changing the mode. If it recurs, the Full Import is not completing; check its Activity.

**"Column 'CHANGE_TYPE' holds 'X', which the change-log configuration for Object Type 'Person' does not account for"**<br />
The application wrote a change-type value you have not listed. Add it to `createValues`, `updateValues` or `deleteValues`, and re-run: the object is reported as an error until it is.

**"Object Type 'Person' has a change-log row with a NULL value in anchor column"**<br />
A trigger logged a row with no key in it, most likely from a related table whose join column was `NULL`. Correct or delete the row, and change the trigger to skip rows with no key; the Delta Import cannot continue while a change it cannot attribute to an object is in the log.

**Objects that were deleted are still in JIM**<br />
Check that the delete path is logged (a bulk `DELETE` fires a statement-level trigger on SQL Server exactly as a single-row one does, but an application that soft-deletes by flag writes an update, not a delete). Where the application soft-deletes, JIM sees an updated row and it is a Synchronisation Rule's scope, not the change log, that should retire the object.

**The Delta Import reports more changes than objects**<br />
Normal. The count JIM reports before reading is a count of change-log rows, which is an upper bound: an object changed three times is three rows and one object.

## Related

- [JIM SQL Connector](jim-sql-connector.md)
- [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md)
- [Run Profiles](../configuration/run-profiles.md)
