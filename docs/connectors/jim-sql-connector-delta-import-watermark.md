# Delta Import with a watermark column

This guide sets up a Delta Import for the [JIM SQL Connector](jim-sql-connector.md) in **Watermark Column** mode. It needs nothing extra in the database beyond a column that moves when a row changes, which many application tables already carry, and it detects creates and updates only. Read [What it cannot see](#what-it-cannot-see) before choosing it.

## How it works

Each Object Type's source, and each of its related tables, names a **watermark column**: a last-modified timestamp or a version number that goes up whenever the row changes. On each Delta Import, JIM:

1. Notes the highest value in each watermark column before reading anything. Those are the watermarks it will save when the run completes, one per source, never one maximum across them all.
2. Selects every row of the Object Type's source whose watermark column has moved past the value saved last time, **or** that has a related-table row whose watermark has, so a phone number replaced or a group membership added is detected as a change to the object it belongs to.
3. Imports each selected object whole, exactly as a Full Import would deliver it. Every one is reported as an update, because a last-modified column cannot tell a create from one; JIM creates the Connected System Object where it holds none.

## What it cannot see

**A deleted row has no column left to move.** Its absence never reaches the query, so a deletion in the source is invisible to this mode, and so is a row that has fallen out of a view. Deletions are found by a **Full Import**, which reads everything and notices what has gone. Schedule one at whatever interval your deprovisioning needs allow; a Delta Import strategy without a periodic Full Import is incomplete in this mode.

The same applies one level down. A row **removed** from a related table is a change to the parent, and it is detected wherever your table records the removal: a soft-delete flag or a tombstone row that moves the row's own watermark, or a watermark on the parent that the application bumps. Where a related table hard-deletes its rows instead, there is nothing left for any watermark to compare, so a revoked membership stays in JIM until the next Full Import.

If deletions must reach JIM promptly, use a [change-log table](jim-sql-connector-delta-import-change-log.md) instead.

## 1. Choose the watermark columns

The Object Type's source needs one, and **every** related table needs one too; JIM refuses to save the configuration otherwise, because a related row added or removed changes the object without touching its own row, and without a watermark there that change could never be detected.

Any column type with a total order works: a timestamp, a whole number, a version counter. It must only ever go up.

```sql title="Microsoft SQL Server: add a last-modified column"
ALTER TABLE HR.EMPLOYEES
    ADD LAST_MODIFIED DATETIME2 NOT NULL CONSTRAINT DF_EMPLOYEES_LAST_MODIFIED DEFAULT SYSUTCDATETIME();
ALTER TABLE HR.EMPLOYEE_PHONES
    ADD LAST_MODIFIED DATETIME2 NOT NULL CONSTRAINT DF_EMPLOYEE_PHONES_LAST_MODIFIED DEFAULT SYSUTCDATETIME();
CREATE INDEX IX_EMPLOYEES_LAST_MODIFIED ON HR.EMPLOYEES (LAST_MODIFIED);
CREATE INDEX IX_EMPLOYEE_PHONES_LAST_MODIFIED ON HR.EMPLOYEE_PHONES (LAST_MODIFIED);
```

```sql title="Oracle Database: add a last-modified column"
ALTER TABLE HR.EMPLOYEES ADD LAST_MODIFIED TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL;
ALTER TABLE HR.EMPLOYEE_PHONES ADD LAST_MODIFIED TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL;
CREATE INDEX HR.IX_EMPLOYEES_LAST_MODIFIED ON HR.EMPLOYEES (LAST_MODIFIED);
CREATE INDEX HR.IX_EMPLOYEE_PHONES_LAST_MODIFIED ON HR.EMPLOYEE_PHONES (LAST_MODIFIED);
```

Index the column: JIM's query is `WHERE LAST_MODIFIED > :watermark`, and without an index that is a table scan on every Delta Import.

!!! tip "SQL Server's rowversion"
    A `rowversion` column is maintained by SQL Server itself, is unique and increasing across the whole database, and needs no trigger, which makes it a good watermark. JIM's schema discovery records it as a Binary attribute, which is correct; as a watermark it is compared as the database compares it.

## 2. Keep it moving

A default fills the column on insert. Updates need a trigger unless the application sets the column itself, and applications that do are the exception.

```sql title="Microsoft SQL Server"
CREATE TRIGGER HR.TR_EMPLOYEES_LAST_MODIFIED
ON HR.EMPLOYEES
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE e SET LAST_MODIFIED = SYSUTCDATETIME()
    FROM HR.EMPLOYEES AS e
    INNER JOIN inserted AS i ON i.EMPLOYEE_ID = e.EMPLOYEE_ID;
END;
GO
```

```sql title="Oracle Database"
CREATE OR REPLACE TRIGGER HR.TRG_EMPLOYEES_LAST_MODIFIED
BEFORE UPDATE ON HR.EMPLOYEES
FOR EACH ROW
BEGIN
    :NEW.LAST_MODIFIED := SYSTIMESTAMP;
END;
/
```

Repeat for each related table. A related table whose rows are only ever inserted and deleted (a membership table, typically) needs no update trigger, but does need the default, so an added row is seen; see [What it cannot see](#what-it-cannot-see) for the removed one.

Where the Object Type reads from a **view**, expose the underlying table's watermark column through the view and name that.

## 3. Configure the Object Type

Add a `watermarkColumn` to every Object Type that is selected for synchronisation, and to every related table of those Object Types, in the Connected System's **Object Types** document. An Object Type that is not selected (a table JIM only exports to, say) takes no part in a Delta Import and needs none; nothing outside JIM changes such a table, so there is nothing to detect.

```json title="Object Types with watermark columns"
{
  "objectTypes": [
    {
      "name": "Person",
      "schema": "HR",
      "table": "V_EMPLOYEES",
      "anchorColumns": [ "EMPLOYEE_ID" ],
      "watermarkColumn": "LAST_MODIFIED",
      "relatedTables": [
        {
          "attributeName": "PhoneNumbers",
          "schema": "HR",
          "table": "EMPLOYEE_PHONES",
          "valueColumn": "PHONE_NUMBER",
          "joinColumns": [ "EMPLOYEE_ID" ],
          "watermarkColumn": "LAST_MODIFIED"
        }
      ]
    }
  ]
}
```

## 4. Choose the mode and save

On the Connected System's Settings tab, set **Delta Import Mode** to **Watermark Column** and save. Any selected Object Type, or related table of one, without a `watermarkColumn` is reported now, naming it. The same check runs when you select an Object Type on the Schema tab (or through the REST API or PowerShell), so selecting one that lacks a watermark column while this mode is set is refused at that point rather than by the next Delta Import; deselect it, or give it a watermark column.

## 5. Baseline with a Full Import

Create a **Full Import** Run Profile and a **Delta Import** Run Profile, then run the Full Import once. A Full Import records every watermark column's current high value **before it reads a single row**, so a change made during the Full Import is read by the first Delta Import rather than lost between the two. Run the Delta Import first instead and JIM performs a Full Import in its place, with a warning on the Activity saying so.

## 6. Schedule both

Schedule the Delta Import at the cadence the application warrants, and a Full Import at the longest interval your deprovisioning can tolerate, because the Full Import is what finds deletions. See [Schedules](../configuration/schedules.md).

## Troubleshooting

**"`Delta Import Mode` is 'Watermark Column', but Object Type 'Person' has related table attribute 'PhoneNumbers' with no 'watermarkColumn'"**<br />
Every related table needs one. Add the column to the table (a default is enough for insert-only tables) and name it in the configuration.

**A Delta Import read every row**<br />
Either no watermark existed yet (the first run, or the first after changing the mode, which is expected and reported as a warning) or a related table had no watermark recorded, in which case JIM correlates on existence alone for that run: one expensive run beats a missed change. The next run is incremental.

**A change to a phone number or a membership was not picked up**<br />
The related table's watermark did not move. Check the default fills the column on insert, the trigger updates it on update, and, for a removal, that the table records it somewhere a watermark can see; a hard delete is invisible until the next Full Import.

**Deleted employees are still in JIM**<br />
By design in this mode. Run, or schedule, a Full Import; or move to a change-log table.

**A row updated during a long transaction was missed**<br />
JIM captures the highest watermark value at the start of the run. A row given a lower value inside a transaction that commits after that capture is behind the watermark and is not read until a Full Import. Keep write transactions short.

## Related

- [JIM SQL Connector](jim-sql-connector.md)
- [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md)
- [Run Profiles](../configuration/run-profiles.md)
