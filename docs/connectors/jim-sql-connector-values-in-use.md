---
title: SQL Connector Values Already in Use
description: How the SQL Connector asks the database whether a Generated Value is already in use, before JIM chooses it.
type: object
audience: administrator
---

# SQL Connector Values Already in Use

Before JIM chooses a [Generated Value](../configuration/generated-values.md) that is exported to this database, the [SQL Connector](jim-sql-connector.md) can ask the database whether the value is already in use, so a row JIM has not imported yet still counts as taken.

When a synchronisation generates a value that is exported unchanged to a column of this database, such as an account name sent to a `USERNAME` column, JIM asks the database whether the value is already in use before choosing it. This catches rows JIM has not imported yet: one inserted by the application since the last import, or by hand. How generated values are checked, and what is and is not probed, is in [Checking availability in target systems](../configuration/generated-values-in-target-systems.md#checking-availability-in-target-systems).

- **One connection per synchronisation**<br /> The connection is made with this Connected System's own settings, credentials and certificate trust, the first time the synchronisation needs it, and closed when the run ends.
- **One statement per object**<br /> The object's candidate values (up to ten) and a control value JIM already holds for the attribute go in one query, every value bound as a parameter: `SELECT DISTINCT [USERNAME] FROM [HR].[EMPLOYEES] WHERE [USERNAME] IN (@probe0, @probe1, ...)`. The column is compared as it stands, so an index on it serves the query. A control value that does not come back means the account cannot see the rows, and JIM stops trusting the probe for that attribute for the rest of the run.
- **It searches what the Object Type reads**<br /> The Object Type's table, view or `select` statement, as configured; a multi-valued attribute is searched in its related table. A view or statement that filters rows out (inactive staff, say) hides them from the probe too, so if a value must be unique across every row, point the Object Type at the table.
- **Case is ignored**<br /> As at every uniqueness check, `JBloggs` in the database stops JIM choosing `jbloggs`, whatever the column's collation. Where the column's collation already ignores case (Microsoft SQL Server's default), JIM compares the column as it stands, so an index on it serves the query. Everywhere else (Oracle Database, a case-sensitive or binary SQL Server collation, or a `select` statement) it compares `LOWER(column)` with the lower-cased values; on a large table, an index on `LOWER(column)` lets the database answer from the index rather than reading every row.
- **Undetermined is not a failure**<br /> A database that cannot be reached, refuses the query (a missing `SELECT` grant, say) or does not answer in time is reported once on the synchronisation's Activity as a warning naming the reason, and the run continues with JIM's own records for that Connected System.

## :material-book-open-variant-outline: Related

- [JIM SQL Connector](jim-sql-connector.md)
- [Generated Values in Target Systems](../configuration/generated-values-in-target-systems.md): probing and Collision Remediation for every Connector.
