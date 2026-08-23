# JIM SQL Connector

## Overview

The JIM SQL Connector connects JIM to identity data held in a relational database: an HR or payroll system, a student record system, or any line-of-business application that keeps its users, groups and departments in tables. JIM can read those tables as a source of identity, write to them as a target, or both.

**Capabilities:** Full Import, Delta Import, Export

One Connected System covers one database, and can synchronise several of its tables at once. You describe which tables (or views) hold which kind of object in a short JSON document; JIM discovers the columns and their types from the database itself.

!!! note "This page is about databases JIM synchronises with"
    JIM's own data store is PostgreSQL and is configured when JIM is deployed; see [Deployment](../administration/deployment.md). Nothing on this page affects it.

## Supported databases

| Database | Connection | Notes |
|----------|------------|-------|
| **Microsoft SQL Server** | Host, optional port, database name; named instances supported (`SERVER\INSTANCE`) | Encrypted by default. Identity columns and defaults are read back when JIM creates a row. |
| **Oracle Database** | Host, optional port, service name or SID | Encrypted by default with Native Network Encryption. Sequence-backed and identity keys are read back when JIM creates a row. Oracle Database Free 23ai works without changes. |

Both databases use the same settings and the same Object Types document, and behave the same way; only the connection details differ. Support for PostgreSQL and MySQL is planned as further dialects of this same connector; see [Wider database support](#wider-database-support).

## What the connector can do

| Capability | Supported | What it means for you |
|---|---|---|
| Full Import | ✅ | Reads every row of each table or view you have selected. Large tables are read a page at a time, so a table of half a million rows is a routine import. See [Full Import](#full-import). |
| Delta Import | ✅ | Reads only what has changed since the last import, using either a change-log table your database maintains or a last-modified column. See [Delta Import](#delta-import). |
| Export | ✅ | Creates, updates and deletes rows, including the rows of related tables (phone numbers, group members). See [Export](#export). |
| Multi-valued attributes | ✅ | A table holding one row per value (a person's phone numbers, a group's members) becomes a multi-valued attribute on the parent object. |
| References | ✅ | A column holding another row's key (a manager, a department) is synchronised as a reference to that object, not as a bare number. |
| Paging | ✅ | The Run Profile's Page Size sets how many rows are read per query. |
| Partitions and Containers | ❌ | Not applicable to databases. Each Object Type names its own table, which is all the scoping a database needs. |
| Parallel Export | ❌ | Exports are written one object at a time, each in its own transaction. |
| Password set | ❌ | JIM does not write passwords to databases. Applications that hold passwords in a table store them in their own format, which JIM cannot safely produce. |

## Before you begin

You will need:

- **A database account for JIM**, with only the permissions the Run Profiles you plan to run need: read access for import, read and write access for export. Concrete grants for both databases are in [Database account permissions](#database-account-permissions). Create a dedicated account rather than sharing one.
- **Network access** from the JIM host to the database server on its listening port (1433 for SQL Server, 1521 for Oracle, or whatever your DBA has set).
- **The server's certificate authority**, if the database uses a certificate your operating system does not already trust (an internal CA or a self-signed certificate). Add it under **Admin > Certificates** first, or add it when the connection test shows you the certificate; see [When the connection test fails on the certificate](#when-the-connection-test-fails-on-the-certificate).
- **To know your tables**: which table or view holds each kind of object, which column (or columns) identifies a row, and which columns hold other rows' keys. A DBA can usually answer this in minutes.
- **The time zone the database records in**, if it stores dates and times without an offset. Most do; see [Dates and times](#dates-and-times).

## Setting up a Connected System

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
| Oracle Encryption | Oracle | Native Network Encryption | **Native Network Encryption** encrypts the session on the ordinary listener with no certificates to manage, and is how most Oracle estates encrypt client traffic. **TCPS (TLS)** uses a TLS listener (usually port 2484) and a server certificate. **None** sends data unencrypted; use it only in a lab. |
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
| Object Types | Yes | The JSON document describing your tables. Step 2 explains it. |

#### Delta Import

| Setting | Required | What to enter |
|---------|----------|---------------|
| Delta Import Mode | No | **Change-Log Table** or **Watermark Column**, once you have set up the database side for one of them. Leave it blank if this Connected System will only run Full Imports. See [Delta Import](#delta-import). |

### Step 2: Describe your tables

The **Object Types** setting tells JIM which tables hold which objects. Each entry names an Object Type, the table or view it comes from, and the column or columns that identify a row. The example below describes a person whose manager is another person, with phone numbers held in a child table:

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

You do not list the ordinary columns; JIM discovers them. You only say what the database cannot: which table is which object, what identifies a row, which columns point at other objects, and which child tables belong to which parent.

The document is checked when you save. A mistake is reported immediately, naming the Object Type and field, and an unrecognised field name is treated as a mistake rather than ignored, so a typo cannot silently leave data out.

#### Object Type fields

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

#### Related table fields

A related table turns a child table into a multi-valued attribute. The commonest case is group membership: a `GROUP_MEMBERS` table of `(GROUP_ID, EMPLOYEE_ID)` becomes a `Members` attribute on the `Group` Object Type, and with `referencesObjectType` each member is a reference to a `Person`.

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
- **Each attribute's data type**, decided from the column's SQL type; the full mapping is in [Type mapping](#type-mapping). The attribute's Description tells you the column type it was decided from (`Source column type: NUMBER(10).`), and where the database cannot be definitive (Oracle's `NUMBER` columns in particular) you can change the type on the Schema tab. See [Attribute data types](../configuration/connected-systems.md#attribute-data-types) for when you would want to.
- **Writability.** Columns of a table are writable; the key columns are set when JIM creates a row and never changed afterwards; columns of a view or a `select` statement are read-only.
- **Reference hints.** Where a table has a foreign key to another Object Type's key column, the attribute's Description says so and shows you the `referencesObjectType` line to add to your document. JIM suggests; you decide.
- **Columns JIM cannot synchronise.** A column of a type JIM has no equivalent for (`xml`, `geography`, and the others listed under [Type mapping](#type-mapping)) is left out, and the schema import's warnings name it. Expose it through a view that casts it to a supported type if you need it.

### Step 4: Run Profiles and Synchronisation Rules

Create [Run Profiles](../configuration/run-profiles.md) for the operations you need (Full Import, Delta Import, Export) and [Synchronisation Rules](../configuration/synchronisation-rules.md) to say what flows where, exactly as for any other Connected System. The sections below describe how each operation behaves against a database, and what to expect.

## How the connector behaves

### Full Import

A Full Import reads every row of each selected Object Type, a page at a time (the Run Profile's Page Size), and reads the related tables for each page in one query rather than one per row. The first page of each Object Type reports the row count, so the Activity shows a real percentage and time remaining.

Most problems affect one row and are reported against that one object, leaving the rest of the import to continue: a value that cannot be converted to the attribute's type, for example, or a fractional value in a column you have recorded as a whole number (JIM reports it rather than rounding it). A problem that would affect every row fails the run with a message saying what to change: a table the account cannot see, a `NULL` in an anchor column, or a number too wide for JIM to hold exactly.

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
| Reference | Writes the referenced object's own key. If the referenced object does not exist in this database yet, JIM writes the rest of the object now and fills the reference in on a later run, once it does; see [Unresolved reference handling](../configuration/connected-systems.md#unresolved-reference-handling) for how a reference that cannot be resolved is reported. |

JIM checks every write took effect. A statement the database accepted but which changed nothing (a trigger discarding the insert, a row deleted outside JIM) is reported as a failure for that object, with a message saying what to check, rather than being recorded as done. One object's failure never stops the rest of the batch, and the failed export is retried with JIM's normal backoff.

Because a committed database transaction is a confirmed write, exports to a database do not need a confirming import.

!!! note "Views and SELECT statements are read-only"
    An Object Type read from a view or a `select` statement cannot be exported to. If the application reads through a view but accepts writes to the table, point the Object Type at the table.

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

- **An Oracle `NUMBER(10)` key is a Long Number, not a Number.** Oracle's single `NUMBER` type can only be narrowed by its declared precision, and ten digits is more than a 32-bit number holds. If such a column needs to flow to a built-in Metaverse Attribute that is a Number (`Employee Number` is the usual one), change the attribute's type to Number on the Schema tab; the procedure is in [Attribute data types](../configuration/connected-systems.md#attribute-data-types).
- **Floating-point columns are approximate.** `float`, `real`, `BINARY_FLOAT` and `BINARY_DOUBLE` are read as Decimal so that they can be compared numerically, but a binary floating-point value does not round-trip to decimal exactly. Prefer an exact numeric column where JIM is authoritative for the value.

A Decimal attribute holds 28 significant digits. A value wider than that is reported rather than rounded, naming the column. Column types JIM has no equivalent for (`xml`, `XMLTYPE`, `sql_variant`, `hierarchyid`, `geography`, `geometry`, `INTERVAL`, `time`, `ROWID`, `BFILE`) are left out of the schema with a warning; expose them through a view that casts to a supported type if they need synchronising.

### Dates and times

JIM stores every date and time in UTC. A column that carries its own offset (`datetimeoffset`, `TIMESTAMP WITH TIME ZONE`) is converted exactly. A column that does not (`datetime2`, `DATE`, `TIMESTAMP`) is read as being in the Connected System's **Database Time Zone**, and written back in that zone on export. The default, `UTC`, is right for a database whose server clock is UTC; enter the IANA name of the zone (`Europe/London`, `Australia/Sydney`) for an application that records local time.

In a zone with daylight saving, the hour the clocks skip in spring and the hour they repeat in autumn are handled for you: a value in the skipped hour is read with the offset in force just before the clocks moved, as PostgreSQL and Java read it, rather than failing the row; a value in the repeated hour is taken as standard time.

On Oracle, JIM also sets the database session's time zone to the same value, which is what makes `TIMESTAMP WITH LOCAL TIME ZONE` columns read correctly. If the Oracle server does not recognise the zone name you entered, the connection is refused with a message saying so.

## Worked examples

### Microsoft SQL Server: an HR application

`dbo.Employees` has an `IDENTITY` key, a `ManagerId` pointing at another employee and a `DepartmentId` pointing at `dbo.Departments`; phone numbers are in `dbo.EmployeePhones`.

```json title="Object Types for a SQL Server HR schema"
{
  "objectTypes": [
    {
      "name": "Person",
      "schema": "dbo",
      "table": "Employees",
      "anchorColumns": [ "EmployeeId" ],
      "columns": [
        { "name": "ManagerId", "referencesObjectType": "Person" },
        { "name": "DepartmentId", "referencesObjectType": "Department" }
      ],
      "relatedTables": [
        {
          "attributeName": "PhoneNumbers",
          "schema": "dbo",
          "table": "EmployeePhones",
          "valueColumn": "PhoneNumber",
          "joinColumns": [ "EmployeeId" ]
        }
      ]
    },
    {
      "name": "Department",
      "schema": "dbo",
      "table": "Departments",
      "anchorColumns": [ "DepartmentId" ]
    }
  ]
}
```

After the schema import: `EmployeeId` (`int`) is the External ID and is set only when a row is created; `ManagerId` and `DepartmentId` are references; `PhoneNumbers` is a multi-valued Text attribute; `HireDate` (`datetime2`) is read in the Database Time Zone; `IsActive` (`bit`) is a Boolean. Exporting a new `Person` inserts the row, reads the identity value back as the External ID, then inserts the phone numbers.

### Oracle Database: an HR schema with groups

`HR.EMPLOYEES` has a sequence-backed `EMPLOYEE_ID NUMBER(10)` key and a `MANAGER_ID`; `HR.GROUPS` has members in `HR.GROUP_MEMBERS`.

```json title="Object Types for an Oracle HR schema"
{
  "objectTypes": [
    {
      "name": "Person",
      "schema": "HR",
      "table": "EMPLOYEES",
      "anchorColumns": [ "EMPLOYEE_ID" ],
      "columns": [
        { "name": "MANAGER_ID", "referencesObjectType": "Person" }
      ]
    },
    {
      "name": "Group",
      "schema": "HR",
      "table": "GROUPS",
      "anchorColumns": [ "GROUP_ID" ],
      "relatedTables": [
        {
          "attributeName": "Members",
          "schema": "HR",
          "table": "GROUP_MEMBERS",
          "valueColumn": "EMPLOYEE_ID",
          "joinColumns": [ "GROUP_ID" ],
          "referencesObjectType": "Person"
        }
      ]
    }
  ]
}
```

After the schema import: `EMPLOYEE_ID` is a Long Number (change it to Number on the Schema tab if it is to flow to `Employee Number`); `IS_ACTIVE NUMBER(1)` is a Number unless **Treat NUMBER(1) Columns as Boolean** is on; `HIRE_DATE` (`DATE`) is read in the Database Time Zone; `Members` is a multi-valued reference to `Person`. Exporting a `Group` inserts its row and one `GROUP_MEMBERS` row per member in one transaction.

!!! tip "Reading through a view and writing to a table"
    If the application publishes a view for readers but takes writes on the underlying table, either point one Connected System at the table for both directions, or use two Connected Systems: one on the view for import, one on the table for export. A single Object Type reads and writes the same source.

## Security

### Database account permissions

Create a dedicated database account for JIM and grant it only what the Run Profiles you configure will use:

- **For schema discovery and import**<br /> `SELECT` on each table or view in the Object Types document, on each related table, and (for Change-Log Table Delta Imports) on each change-log table. JIM reads the database's own catalogue views, which show the account exactly the objects it can already read; no wider catalogue permission is needed.
- **For export**<br /> `INSERT`, `UPDATE` and `DELETE` on each Object Type's table and each related table, in addition to `SELECT`.
- **Nothing else.**<br /> JIM creates no objects and changes no schema. It does not need `db_owner`, `DBA`, or `SELECT ANY TABLE`.

```sql title="Microsoft SQL Server: an import-only account"
CREATE LOGIN jim_sync WITH PASSWORD = '<a strong password>';
CREATE USER jim_sync FOR LOGIN jim_sync;
GRANT SELECT ON SCHEMA::HR TO jim_sync;
```

```sql title="Microsoft SQL Server: an account that also exports"
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::HR TO jim_sync;
```

```sql title="Oracle Database: an import-only account"
CREATE USER jim_sync IDENTIFIED BY "<a strong password>";
GRANT CREATE SESSION TO jim_sync;
GRANT SELECT ON HR.V_EMPLOYEES TO jim_sync;
GRANT SELECT ON HR.EMPLOYEE_PHONES TO jim_sync;
```

```sql title="Oracle Database: an account that also exports"
GRANT SELECT, INSERT, UPDATE, DELETE ON HR.EMPLOYEES TO jim_sync;
GRANT SELECT, INSERT, UPDATE, DELETE ON HR.EMPLOYEE_PHONES TO jim_sync;
```

On Oracle, grant per object rather than `SELECT ANY TABLE`. The account then sees exactly what JIM should, and a schema import that reports a table "cannot be seen" means a grant is missing, not that something is wrong with JIM.

### Encryption in transit

- **SQL Server** connections are encrypted by default and fail rather than fall back to plain text. The server's certificate is always validated; JIM never accepts an unvalidated certificate.
- **Oracle** connections use Native Network Encryption by default (AES, with SHA-2 integrity checking), or TLS over a TCPS listener if you choose it. Choose **None** only in a lab.
- Nothing needs installing on the JIM host for either database, and JIM connects only to the host you name, so the connector works unchanged in an air-gapped deployment.

#### When the connection test fails on the certificate

For SQL Server, if the server presents a certificate JIM does not yet trust (one issued by an internal authority, or self-signed), the connection test fails and shows you the certificate: who issued it, what name it was issued for, its validity dates and its thumbprint, and which check it failed. Add the issuing authority (or, for a self-signed certificate, the certificate itself) under **Admin > Certificates** and test again. An expired certificate, or one issued for a different name from the host you connect to, is still refused; renew it, or connect by a name the certificate carries.

For Oracle over TCPS, JIM shows the same detail, but the certificate must be one your operating system already trusts; the Oracle driver does not accept additional trust anchors. Native Network Encryption needs no certificate at all, which is one reason it is the default.

### Credentials and logging

The password is stored encrypted and is used only to open the connection. It does not appear in any log, Activity or configuration history, and the database driver's error messages never include it.

### What you are trusting

The Object Types document, including any `select` statement, is administrator configuration: it decides which tables JIM reads and writes, just as a Synchronisation Rule decides what flows where. Everything JIM sends to the database as data is sent as a parameter, never pasted into a statement, and every table and column name is validated and quoted.

## Licensing notice

The Oracle dialect uses **Oracle Data Provider for .NET, Managed Driver** (`Oracle.ManagedDataAccess.Core`), which Oracle distributes under the **Oracle Free Distribution, Hosting, and Use Terms and Conditions**. Those terms allow the driver to be redistributed unmodified and used in your business operations at no charge, provided a copy of the terms accompanies any distribution and Oracle's notices are left in place. JIM ships a copy of the terms in every image at `/app/third-party-notices/` and in the repository under [`third-party-notices/`](https://github.com/TetronIO/JIM/tree/main/third-party-notices). If your organisation has its own licence agreement with Oracle covering the driver, that agreement applies instead. Please read the terms; this notice is not legal advice.

The SQL Server dialect uses `Microsoft.Data.SqlClient`, which is MIT licensed.

## Wider database support

PostgreSQL and MySQL are planned as further dialects of this connector, with the same settings and the same Object Types document; see the [roadmap](../reference/roadmap.md). If you need another database engine, or need one of those sooner, tell us in the [Ideas category of GitHub Discussions](https://github.com/TetronIO/JIM/discussions/categories/ideas); what comes next is decided by who asks.

## Troubleshooting

**"Unable to connect. Message: ..."**<br />
The database driver's own message follows. A wrong host or blocked port times out after the Connection Timeout; a wrong database name, service name or SID is rejected immediately in the database's own words; a wrong password is an authentication failure. On Oracle, check you have chosen the right form (service name or SID) as well as the right value.

**The connection test fails on the certificate (SQL Server)**<br />
See [When the connection test fails on the certificate](#when-the-connection-test-fails-on-the-certificate).

**"Object Type 'X' reads from HR.TABLE, which this database account cannot see"**<br />
The name or schema is wrong, or the account has not been granted `SELECT` on it. On Oracle, grants are per table; see [Database account permissions](#database-account-permissions).

**"Object Type 'X' names 'TABLE', which exists in more than one schema"**<br />
Add `"schema"` to that Object Type.

**An attribute is missing after the schema import**<br />
Check the import's warnings: a column whose type JIM cannot synchronise is named there. Expose it through a view that casts it to a supported type.

**"The value has a fractional part, and this attribute's data type is Number"**<br />
The column holds fractional values but the attribute's type is a whole number, usually because the type was changed on the Schema tab. Change it to Decimal, or correct the source data.

**A Delta Import ran as a Full Import**<br />
Expected on the first run, and after changing the Delta Import Mode. If it happens every time, read the warning on the Activity for the reason.

**Deleted rows are not removed by a Delta Import**<br />
Watermark Column mode cannot see deletions. Use a change-log table, or schedule a periodic Full Import. See [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md).

**An export reports "No row was written ... though the database raised no error"**<br />
A trigger or rule on the table discarded the insert, or the account cannot write to the table. Check the table's triggers and the account's grants.

**An export reports "No row ... is identified by this Connected System Object's external ID"**<br />
The row was deleted or re-keyed outside JIM. Run a Full Import to bring JIM back in line with the table.

**An Oracle export reports "returned no value for its anchor column"**<br />
The table's key is neither flowed by a Synchronisation Rule nor generated by the database. Back the column with a sequence-based default or an identity column, or flow the key from JIM.

**Dates are out by an hour**<br />
The column carries no offset and the Database Time Zone does not match what the application records. Set it to the zone the application uses; columns that carry their own offset are unaffected.

## Related

- [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md)
- [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md)
- [Attribute data types](../configuration/connected-systems.md#attribute-data-types)
- [Connected Systems](../configuration/connected-systems.md)
- [Connectors](index.md)
