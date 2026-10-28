---
title: JIM SQL Connector
description: Synchronise identities with tables in Microsoft SQL Server and Oracle Database, as a source, a target or both, and the settings it takes.
type: object
audience: administrator
---

# JIM SQL Connector

The JIM SQL Connector connects JIM to identity data held in a relational database: an HR or payroll system, a student record system, or any line-of-business application that keeps its users, groups and departments in tables. JIM can read those tables as a source of identity, write to them as a target, or both.

**Capabilities:** Full Import, Delta Import, Export, Probe

One Connected System covers one database, and can synchronise several of its tables at once. You describe which tables (or views) hold which kind of object in a short JSON document; JIM discovers the columns and their types from the database itself.

```jim-diagram
archetype: anatomy
title: What one entry in the Object Types document says
desc: "Each entry in the Object Types document describes one Object Type, and says only what the database cannot. name is what JIM calls the Object Type. table, or a select statement, is where its rows come from; an Object Type read through select can be imported but not exported to. anchorColumns are the columns that identify a row, and become its External ID. columns lists the columns holding another object's key, which become references. relatedTables names child tables, each of which becomes a multi-valued attribute. watermarkColumn or changeLog is needed only for a Delta Import. JIM discovers the ordinary columns and their types itself, and checks the document when you save it."
subject:
  kind: object
  label: One Object Type entry
  parts:
    - { label: "`name`", detail: what JIM calls the Object Type }
    - { label: "`table` or `select`", detail: "where its rows come from; `select` is import only" }
    - { label: "`anchorColumns`", detail: "identify a row: the External ID" }
    - { label: "`columns`", detail: "another object's key: a reference" }
    - { label: "`relatedTables`", detail: "a child table: a multi-valued attribute" }
    - { label: "`watermarkColumn` or `changeLog`", detail: for Delta Import only }
properties:
  - { name: Ordinary columns, value: discovered by JIM }
  - { name: Checked, value: when you save }
```

!!! note "This page is about databases JIM synchronises with"
    JIM's own data store is PostgreSQL and is configured when JIM is deployed; see [Deployment](../administration/deployment.md). Nothing on this page affects it.

## :material-database-outline: Supported databases

| Database | Connection | Notes |
|----------|------------|-------|
| **Microsoft SQL Server** | Host, optional port, database name; named instances supported (`SERVER\INSTANCE`) | Encrypted by default. Identity columns and defaults are read back when JIM creates a row. |
| **Oracle Database** | Host, optional port, service name or SID | Encrypted by default with Native Network Encryption. Sequence-backed and identity keys are read back when JIM creates a row. Oracle Database Free 23ai works without changes. |

Both databases use the same settings and the same Object Types document, and behave the same way; only the connection details differ. For other database engines, see [Wider database support](#wider-database-support).

## :material-format-list-checks: What the connector can do

| Capability | Supported | What it means for you |
|---|---|---|
| Full Import | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Reads every row of each table or view you have selected. Large tables are read a page at a time, so a table of half a million rows is a routine import. See [Full Import](#full-import). |
| Delta Import | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Reads only what has changed since the last import, using either a change-log table your database maintains or a last-modified column. See [Delta Import](#delta-import). |
| Export | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Creates, updates and deletes rows, including the rows of related tables (phone numbers, group members). See [Export](#export). |
| Multi-valued attributes | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | A table holding one row per value (a person's phone numbers, a group's members) becomes a multi-valued attribute on the parent object. |
| References | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | A column holding another row's key (a manager, a department) is synchronised as a reference to that object, not as a bare number. |
| Paging | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | The Run Profile's Page Size sets how many rows are read per query. |
| Partitions and Containers | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } | Not applicable to databases. Each Object Type names its own table, which is all the scoping a database needs. |
| Parallel Export | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } | Exports are written one object at a time, each in its own transaction. |
| Password set | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } | JIM does not write passwords to databases. Applications that hold passwords in a table store them in their own format, which JIM cannot safely produce. |
| Probe | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } | Asks the database whether a generated value is already in use before JIM chooses it. See [Probing for values already in use](jim-sql-connector-values-in-use.md). |

## :material-clipboard-list-outline: Before you begin

You will need:

- **A database account for JIM**, with only the permissions the Run Profiles you plan to run need: read access for import, read and write access for export. Concrete grants for both databases are in [Database account permissions](jim-sql-connector-security.md#database-account-permissions). Create a dedicated account rather than sharing one.
- **Network access** from the JIM host to the database server on its listening port (1433 for SQL Server, 1521 for Oracle, or whatever your DBA has set).
- **The server's certificate authority**, if the database uses a certificate your operating system does not already trust (an internal CA or a self-signed certificate). Add it under **Admin > Certificates** first, or add it when the connection test shows you the certificate; see [When the connection test fails on the certificate](jim-sql-connector-security.md#when-the-connection-test-fails-on-the-certificate).
- **To know your tables**: which table or view holds each kind of object, which column (or columns) identifies a row, and which columns hold other rows' keys. A DBA can usually answer this in minutes.
- **The time zone the database records in**, if it stores dates and times without an offset. Most do; see [Dates and times](#dates-and-times).

## :material-server-network-outline: Setting up a Connected System

### Step 1: Create the Connected System

Create a Connected System and choose **JIM SQL Connector**. Fill in the settings below, then use **Test Connection** before going further; it confirms the host, credentials and encryption in one step.

#### Database Server

| Setting | Required | What to enter |
|---------|----------|---------------|
| Database Type | Yes | **Microsoft SQL Server** or **Oracle Database**. The settings that follow change to suit. |
| Host | Yes | The database server's hostname or IP address. For a named SQL Server instance, `SERVER\INSTANCE`. |
| Port | No | Leave blank for the default (1433 for SQL Server; 1521 for Oracle, or 2484 for Oracle over TLS). |
| Database Name | SQL Server | The database on that server. |
| Oracle Database Identified By | Oracle | **Service Name** (use this unless your DBA gives you a SID) or **SID**. |
| Oracle Service Name | Oracle, by service name | For example `HRPROD.example.com`. On Oracle Database Free the pluggable database is `FREEPDB1`. |
| Oracle SID | Oracle, by SID | For example `HRPROD`. |

JIM builds the connection string from these values; there is nothing to paste, and the connection details never appear in a log.

#### Credentials

| Setting | Required | What to enter |
|---------|----------|---------------|
| Username | Yes | The database account JIM connects as. |
| Password | Yes | Its password. Stored encrypted and never shown or logged again. |

#### Transport Security

| Setting | Applies to | Default | What it does |
|---------|------------|---------|--------------|
| Encrypt Connection | SQL Server | On | Encrypts the connection with TLS. If the server cannot encrypt, the connection fails rather than falling back to plain text. The server's certificate is checked against your operating system's trusted authorities and any certificates you have added under **Admin > Certificates**. |
| Oracle Encryption | Oracle | Native Network Encryption | **Native Network Encryption** encrypts the session on the ordinary listener with no certificates to manage, and is how most Oracle estates encrypt client traffic. **TCPS (TLS)** uses a TLS listener (usually port 2484) and a server certificate; one from your own certificate authority works once the root of its chain is trusted in JIM. **None** sends data unencrypted; use it only in a lab. |
| Connection Timeout | Both | 30 seconds | How long to wait for the server before giving up. |

#### Date and Time

| Setting | Default | What to enter |
|---------|---------|---------------|
| Database Time Zone | `UTC` | The time zone that the database's date and time columns are recorded in, for columns that do not carry their own offset. Enter `UTC` or an IANA name such as `Europe/London`. See [Dates and times](#dates-and-times). |

#### Type Mapping (Oracle only)

| Setting | Default | When to turn it on |
|---------|---------|--------------------|
| Treat NUMBER(1) Columns as Boolean | Off | When `NUMBER(1)` columns in this database are yes/no flags. Oracle has no boolean column type, so flags are usually stored this way, but a single-digit column can equally be a small number; the setting applies to every `NUMBER(1)` column, so turn it on only if that is true of all of them. |
| Treat RAW(16) Columns as Guid | Off | When `RAW(16)` columns in this database hold GUIDs rather than other 16-byte values such as hashes. |

Where a database mixes the two meanings, leave the setting off; the columns import as numbers or binary values, and you can convert the ones that are really flags or GUIDs in an Attribute Flow, or expose them through a view under a clearer type.

#### Object Type Configuration

| Setting | Required | What to enter |
|---------|----------|---------------|
| Object Types | Yes | Which tables or views hold each kind of object, and what identifies a row. The portal shows one card per Object Type; Step 2 explains it. |

#### Delta Import

| Setting | Required | What to enter |
|---------|----------|---------------|
| Delta Import Mode | No | **Change-Log Table** or **Watermark Column**, once you have set up the database side for one of them. Leave it blank if this Connected System will only run Full Imports. See [Delta Import](#delta-import). |

### Step 2: Describe your tables

SQL has no way to say which table holds which kind of object, which column identifies a row, or which child table belongs to which parent, so you tell JIM. On the Connected System's **Settings** tab, **Object Types** under **Object Type Configuration** shows one card per Object Type. Select **Add Object Type**, then on its card give it a name and choose:

- its **Schema** and **Table or view**, or **Read from a SELECT statement instead** where you cannot create a view
- its **Anchor**: the column or columns whose values identify a row, in key order
- any **References to other Object Types**: columns whose values hold another object's anchor, such as a manager
- any **Multi-valued attributes**: child tables holding one row per value, such as phone numbers or group members

Names are chosen from the database's own list of tables, views and columns, read with the connection settings on screen. Where the database declares a primary key, the card offers it as the anchor. Where a column has a foreign key to another Object Type's anchor, the card offers it as a reference, and a foreign key to a table no Object Type reads from yet offers to add one for it. Nothing changes until you accept. If JIM cannot reach the database, the card says why, names are typed instead, and **Retry** reads the list again.

You do not list the ordinary columns; JIM discovers them. You only say what the database cannot: which table is which object, what identifies a row, which columns point at other objects, and which child tables belong to which parent. A card shows any problem with its Object Type as you work, and a collapsed card shows how many it has.

#### The Object Types document

Behind the cards, the setting holds a JSON document. It is what REST and PowerShell send as the setting's value, and what **View as JSON** shows. To find the exact names to put in it, list what the database account can see with [`Get-JIMConnectedSystemCatalogue`](../powershell/connected-system-schema.md#get-jimconnectedsystemcatalogue) or the catalogue endpoints of the REST API, once the connection settings are saved. A table's primary key, which it reports in key order, is usually the right `anchorColumns`.

Each entry in the document names an Object Type, the table or view it comes from, and the column or columns that identify a row. The example below describes a person whose manager is another person, with phone numbers held in a child table:

```json title="A Person with a manager reference and a related table of phone numbers"
{
  "objectTypes": [
    {
      "name": "Person",
      "schema": "HR",
      "table": "V_EMPLOYEES",
      "anchorColumns": [ "EMPLOYEE_ID" ],
      "columns": [
        { "name": "MANAGER_EMPLOYEE_ID", "referencesObjectType": "Person" }
      ],
      "relatedTables": [
        {
          "attributeName": "PhoneNumbers",
          "schema": "HR",
          "table": "EMPLOYEE_PHONES",
          "valueColumn": "PHONE_NUMBER",
          "joinColumns": [ "EMPLOYEE_ID" ]
        }
      ]
    }
  ]
}
```

The document is checked when you save. A mistake is reported immediately, naming the Object Type and field, and an unrecognised field name is treated as a mistake rather than ignored, so a typo cannot silently leave data out. Where the unrecognised name is close to a real one, the message suggests it: `Did you mean 'anchorColumns'?`.

##### Object Type fields

| Field | Required | What it is |
|-------|----------|------------|
| `name` | Yes | What JIM will call the Object Type. Use a standard name (`User`, `Group`, `Person`) where one fits, and JIM maps it to the matching Metaverse Object Type automatically. |
| `table` | One of `table` / `select` | The table or view to read from. |
| `schema` | No | The schema the table is in. Leave it out and JIM finds the table by name; if the same name exists in more than one schema, JIM asks you to say which. |
| `select` | One of `table` / `select` | A `SELECT` statement to read from instead of a table or view, for when you cannot create a view. It must be a single statement beginning `SELECT` or `WITH`, with no semicolon. An Object Type read this way can be imported but not exported to. |
| `anchorColumns` | Yes | The column or columns whose values identify a row, in the order of the table's key. |
| `columns` | No | Columns that hold another object's key. Each entry is `{ "name": "...", "referencesObjectType": "..." }`; the referenced Object Type must be in the same document. |
| `relatedTables` | No | Child tables that hold one row per value for a multi-valued attribute. See below. |
| `watermarkColumn` | For Watermark Column Delta Import | The last-modified column on this table; see [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md). |
| `changeLog` | For Change-Log Table Delta Import | The change-log table for this Object Type; see [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md). |

##### Related table fields

A related table turns a child table into a multi-valued attribute. The commonest case is group membership: a `GROUP_MEMBERS` table of `(GROUP_ID, EMPLOYEE_ID)` becomes a `Members` attribute on the `Group` Object Type, and with `referencesObjectType` each member is a reference to a `Person`. Every row is a value, whatever the column's type, so a related table of dates imports each date. A related table of true or false values becomes a single-valued attribute, because a Boolean holds one value; a parent with rows holding both has each value reported rather than one chosen.

| Field | Required | What it is |
|-------|----------|------------|
| `attributeName` | Yes | The name the multi-valued attribute will have on the parent Object Type, for example `PhoneNumbers` or `Members`. It must not be the name of one of the parent's own columns. |
| `table` / `schema` | Yes / No | The child table. |
| `valueColumn` | Yes | The column holding each value. |
| `joinColumns` | Yes | The child table's columns that hold the parent's key, one per anchor column, in the same order. |
| `referencesObjectType` | No | If the values are keys of another Object Type, name it and the attribute becomes a reference. |
| `watermarkColumn` | For Watermark Column Delta Import | The last-modified column on the child table. |

### Step 3: Import the schema and choose what to synchronise

On the Connected System's **Schema** tab, import the schema. JIM reads the database's catalogue for every table in your document and shows one Object Type per entry, with one attribute per column. Select the Object Types and attributes you want to synchronise.

What you will see:

- **The External ID is chosen for you.** The anchor column becomes the Object Type's External ID attribute. Where an Object Type has a two-or-more-column key, JIM composes a single read-only Text attribute from them, named by joining the column names with `+` (`REGION+EMPLOYEE_NO`).
- **Each attribute's data type**, decided from the column's SQL type; the full mapping is in [Type mapping](#type-mapping). The attribute's Description tells you the column type it was decided from (`Source column type: NUMBER(10).`), and where the database cannot be definitive (Oracle's `NUMBER` columns in particular) you can change the type on the Schema tab. See [Attribute data types](../configuration/connected-system-attributes.md#attribute-data-types) for when you would want to.
- **Writability.** Columns of a table are writable; the key columns are set when JIM creates a row and never changed afterwards; columns of a view or a `select` statement are read-only.
- **Reference hints.** Where a table has a foreign key to another Object Type's key column, the attribute's Description says so and shows you the `referencesObjectType` line to add to your document. JIM suggests; you decide.
- **Columns JIM cannot synchronise.** A column of a type JIM has no equivalent for (`xml`, `geography`, and the others listed under [Type mapping](#type-mapping)) is left out, and the schema import's warnings name it. Expose it through a view that casts it to a supported type if you need it.

### Step 4: Run Profiles and Synchronisation Rules

Create [Run Profiles](../configuration/run-profiles.md) for the operations you need (Full Import, Delta Import, Export) and [Synchronisation Rules](../configuration/synchronisation-rules.md) to say what flows where, exactly as for any other Connected System. The sections below describe how each operation behaves against a database, and what to expect.

## :material-cog-outline: How the connector behaves

### Full Import

A Full Import reads every row of each selected Object Type, a page at a time (the Run Profile's Page Size), and reads the related tables for each page in one query rather than one per row. A related table's rows, and a change log's, are matched to their object as the database compares the anchor column: ignoring case, unless its collation is case-sensitive. The first page of each Object Type reports the row count, so the Activity shows a real percentage and time remaining.

Most problems affect one row and are reported against that one object, leaving the rest of the import to continue: a value that cannot be converted to the attribute's type, for example, or a fractional value in a column you have recorded as a whole number (JIM reports it rather than rounding it). Every such value is reported with what the column held, the row's other columns are still read, and JIM leaves the attribute as it already held it; see [Values JIM could not import](../configuration/activity-execution-items.md#values-jim-could-not-import). A problem that would affect every row fails the run with a message saying what to change: a table the account cannot see, a `NULL` in an anchor column, or a number too wide for JIM to hold exactly.

### Delta Import

A database keeps no record of what changed unless you give it one, so a Delta Import needs one of two things on the database side. The **Delta Import Mode** setting says which you have set up:

| Mode | What you provide | What it detects |
|------|------------------|-----------------|
| **Change-Log Table** (recommended) | A table your database writes a row to for every change: the object's key, what kind of change it was, and a sequence number or timestamp. Usually filled by a trigger. | Creates, updates and **deletes**. |
| **Watermark Column** | A last-modified or version column on each table, and on each related table. | Creates and updates. **Deletes are not detected**, because a deleted row has nothing left to compare; schedule a Full Import to pick those up. |

Each mode has a step-by-step guide with the table and trigger definitions for both databases:

- [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md)
- [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md)

!!! note "The first Delta Import runs as a Full Import"
    JIM has nothing to compare against until one import has completed, so the first Delta Import you run (and the first after changing the Delta Import Mode) performs a Full Import instead, with a warning on the Activity saying so. From then on Delta Imports read only changes. A Delta Import run with no mode chosen is refused, so you cannot accidentally schedule one that does a Full Import every time.

### Export

An Export writes each Pending Export to the Object Type's table. Everything belonging to one object (its row and its related-table rows) is written in one transaction, so a row is never left half-written.

| Change | What JIM does |
|--------|---------------|
| Create | Inserts the row, then its related-table rows. If the table generates the key (an identity column, a sequence, a default), JIM reads the generated value back and records it as the object's External ID. If your Synchronisation Rule flows the key (a natural identifier such as an employee number), JIM writes it. |
| Update | Updates only the columns whose values changed. Key columns are never rewritten. |
| Delete | Deletes the related-table rows, then the row. A row that has already gone counts as deleted. |
| Add or remove a multi-valued value | Inserts or deletes one row of the related table. |
| Set a multi-valued attribute to one value | Deletes the object's rows in the related table, then inserts one row holding the value (none, when the attribute is cleared). This is how a single-valued Metaverse attribute flowing to a related table is written, so the table holds exactly that value. |
| Reference | Writes the referenced object's own key. If the referenced object does not exist in this database yet, JIM writes the rest of the object now and fills the reference in on a later run, once it does; see [Unresolved reference handling](../configuration/unresolved-references.md) for how a reference that cannot be resolved is reported. |

JIM checks every write took effect. A statement the database accepted but which changed nothing (a trigger discarding the insert, a row deleted outside JIM) is reported as a failure for that object, with a message saying what to check, rather than being recorded as done. One object's failure never stops the rest of the batch, and the failed export is retried with JIM's normal backoff.

Because a committed database transaction is a confirmed write, exports to a database do not need a confirming import.

!!! note "Views and SELECT statements are read-only"
    An Object Type read from a view or a `select` statement cannot be exported to. If the application reads through a view but accepts writes to the table, point the Object Type at the table.

### Probing for values already in use

Before JIM chooses a Generated Value exported to this database, it can ask the database whether the value is already in use; see [SQL Connector Values Already in Use](jim-sql-connector-values-in-use.md).

### Type mapping

JIM decides each attribute's type from the column's declared SQL type:

| JIM type | Microsoft SQL Server | Oracle Database |
|----------|----------------------|-----------------|
| Text | `char`, `nchar`, `varchar`, `nvarchar`, `text`, `ntext` | `CHAR`, `NCHAR`, `VARCHAR2`, `NVARCHAR2`, `CLOB`, `NCLOB`, `LONG` |
| Number | `int`, `smallint`, `tinyint` | `NUMBER(p,0)` with p up to 9 |
| Long Number | `bigint` | `NUMBER(p,0)` with p from 10 to 18 |
| Decimal | `decimal`, `numeric`, `money`, `smallmoney`, `float`, `real` | `NUMBER(p,0)` with p of 19 or more; `NUMBER(p,s)` with a scale; `NUMBER` with no precision; `BINARY_FLOAT`, `BINARY_DOUBLE` |
| Boolean | `bit` | `NUMBER(1)`, when **Treat NUMBER(1) Columns as Boolean** is on |
| Date/Time | `date`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset` | `DATE`, `TIMESTAMP`, `TIMESTAMP WITH TIME ZONE`, `TIMESTAMP WITH LOCAL TIME ZONE` |
| GUID | `uniqueidentifier` | `RAW(16)`, when **Treat RAW(16) Columns as Guid** is on |
| Binary | `binary`, `varbinary`, `image`, `timestamp` / `rowversion` | `RAW`, `LONG RAW`, `BLOB` |
| Reference | Any column you list under `columns` with `referencesObjectType` | Any column you list under `columns` with `referencesObjectType` |

Two things to know:

- **An Oracle `NUMBER(10)` key is a Long Number, not a Number.** Oracle's single `NUMBER` type can only be narrowed by its declared precision, and ten digits is more than a 32-bit number holds. If such a column needs to flow to a built-in Metaverse Attribute that is a Number (`Employee Number` is the usual one), change the attribute's type to Number on the Schema tab; the procedure is in [Attribute data types](../configuration/connected-system-attributes.md#attribute-data-types).
- **Floating-point columns are approximate.** `float`, `real`, `BINARY_FLOAT` and `BINARY_DOUBLE` are read as Decimal so that they can be compared numerically, but a binary floating-point value does not round-trip to decimal exactly. Prefer an exact numeric column where JIM is authoritative for the value.

A Decimal attribute holds 28 significant digits. A value wider than that is reported rather than rounded, naming the column. Column types JIM has no equivalent for (`xml`, `XMLTYPE`, `sql_variant`, `hierarchyid`, `geography`, `geometry`, `INTERVAL`, `time`, `ROWID`, `BFILE`) are left out of the schema with a warning; expose them through a view that casts to a supported type if they need synchronising.

### Dates and times

JIM stores every date and time in UTC. A column that carries its own offset (`datetimeoffset`, `TIMESTAMP WITH TIME ZONE`) is converted exactly. A column that does not (`datetime2`, `DATE`, `TIMESTAMP`) is read as being in the Connected System's **Database Time Zone**, and written back in that zone on export. The default, `UTC`, is right for a database whose server clock is UTC; enter the IANA name of the zone (`Europe/London`, `Australia/Sydney`) for an application that records local time.

In a zone with daylight saving, the hour the clocks skip in spring and the hour they repeat in autumn are handled for you: a value in the skipped hour is read with the offset in force just before the clocks moved, as PostgreSQL and Java read it, rather than failing the row; a value in the repeated hour is taken as standard time.

On Oracle, JIM also sets the database session's time zone to the same value, which is what makes `TIMESTAMP WITH LOCAL TIME ZONE` columns read correctly. If the Oracle server does not recognise the zone name you entered, the connection is refused with a message saying so.

A date and time attribute value is held to the microsecond (six fractional digits), so the seventh digit of a `datetime2(7)` value is dropped when it is imported, and the same value imported again is not a change.

Schema discovery gives each date column a [Date resolution](../configuration/connected-system-attributes.md#date-resolution) from its type: a `datetime2(3)` column holds milliseconds, a legacy `datetime` ten milliseconds, a SQL Server `date` whole days. JIM exports each date at that resolution and compares at it, so a fraction the column cannot hold is not exported again on every run. Removing a date from a related table removes the rows that fall within that step of the column's resolution, so a legacy `datetime` row stored as `.003` goes when JIM removes the date it sent as `.000`.

The Connector's own Delta Import watermarks and anchors are held more finely, to a ten-millionth of a second (seven fractional digits). An Oracle `TIMESTAMP(9)` column is fine as a Delta Import watermark, change-log sequence or anchor so long as its values carry no more digits than that, which is always the case for values from `SYSTIMESTAMP` (microseconds). A value with genuine nanoseconds is read back shortened, so the row a page or a run stopped on is read again.

## :material-file-document-outline: Worked examples

[SQL Connector Worked Examples](jim-sql-connector-examples.md) configures two databases end to end: an HR application on Microsoft SQL Server, and an HR schema with groups on Oracle Database.

## :material-shield-lock-outline: Security

[SQL Connector Security](jim-sql-connector-security.md) covers the database account's permissions, encryption in transit and a connection test that fails on the certificate, how the password is kept out of logs, and what you are trusting.

## :material-scale-balance: Licensing notice

The Oracle dialect uses **Oracle Data Provider for .NET, Managed Driver** (`Oracle.ManagedDataAccess.Core`), which Oracle distributes under the **Oracle Free Distribution, Hosting, and Use Terms and Conditions**. Those terms allow the driver to be redistributed unmodified and used in your business operations at no charge, provided a copy of the terms accompanies any distribution and Oracle's notices are left in place. JIM ships a copy of the terms in every image at `/app/third-party-notices/` and in the repository under [`third-party-notices/`](https://github.com/TetronIO/JIM/tree/main/third-party-notices). If your organisation has its own licence agreement with Oracle covering the driver, that agreement applies instead. Please read the terms; this notice is not legal advice.

The SQL Server dialect uses `Microsoft.Data.SqlClient`, which is MIT licensed.

## :material-database-plus-outline: Wider database support

The connector supports Microsoft SQL Server and Oracle Database; for other engines, see the [Roadmap](../reference/roadmap.md). If you need another database engine, or need one of those sooner, tell us in the [Ideas category of GitHub Discussions](https://github.com/TetronIO/JIM/discussions/categories/ideas); what comes next is decided by who asks.

## :material-wrench-outline: Troubleshooting

[Troubleshooting the SQL Connector](jim-sql-connector-troubleshooting.md) covers connection, schema import, Delta Import and export failures, each by the message it shows.

## :material-book-open-variant-outline: Related

- [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md)
- [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md)
- [Attribute data types](../configuration/connected-system-attributes.md#attribute-data-types)
- [Connected Systems](../configuration/connected-systems.md)
- [Connectors](index.md)
