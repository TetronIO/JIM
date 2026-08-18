# JIM SQL Connector

## Overview

The JIM SQL Connector synchronises identities with relational databases: HR systems, payroll, student records, and the line-of-business applications that keep their users in a table. It reads and writes **Microsoft SQL Server** and **Oracle Database** through fully managed ADO.NET drivers, so nothing native is installed on the JIM host and JIM stays air-gap deployable.

**Capabilities:** Full Import, Delta Import, Export

One Connected System covers a whole database at once. An **Object Types** document names each table or view JIM should synchronise, the columns that identify a row, the columns that carry another object's identifier as a reference, and any related table whose rows gather onto the parent as a multi-valued attribute. Everything else, the columns and their types, is discovered from the database's own catalogue.

!!! note "Not a JIM database setting"
    This page is about databases JIM synchronises *with*. JIM's own store is PostgreSQL and is configured at deployment; see [Deployment](../administration/deployment.md).

## Supported Databases

| Database | Notes |
|----------|-------|
| **Microsoft SQL Server** | Connected through `Microsoft.Data.SqlClient`. Named instances (`SERVER\INSTANCE`), TLS on by default, database-generated keys read back through `OUTPUT INSERTED`. |
| **Oracle Database** | Connected through `Oracle.ManagedDataAccess.Core` (ODP.NET managed driver). Addressed by service name or SID, Native Network Encryption on by default, database-generated keys read back through `RETURNING ... INTO`. Oracle Database Free 23ai works as-is, and is what JIM's own tests run against. |

The two databases are dialects behind one connector: the same Object Types document, the same import and export behaviour, and the same settings apart from the connection details each one needs. Support for further engines is planned as additional dialects of this same connector rather than as separate connectors; see [Wider database support](#wider-database-support).

## Features

| Capability | Supported | Notes |
|---|---|---|
| Full Import | ✅ | Pages through each table by keyset, so a large table costs the same per page from the first row to the last. See [Full Import](#full-import). |
| Delta Import | ✅ | Reads a change-log table your database maintains, or a last-modified column. See [Delta Import](#delta-import). |
| Export | ✅ | Inserts, updates and deletes rows, each object in a transaction of its own. See [Export](#export). |
| Paging | ✅ | The Run Profile's Page Size is the number of rows read per query. |
| Multi-valued attributes | ✅ | A related table of one row per value (phone numbers, group members) becomes a multi-valued attribute on the parent. |
| References | ✅ | A column holding another row's identifier is a Reference once you say which Object Type it points at; JIM resolves it to the referenced Connected System Object on import and writes that object's own identifier on export. |
| Partitions and Containers | ❌ | A database has no equivalent of a directory's naming contexts; a Connected System addresses one database, and each Object Type names its own table within it. |
| Parallel Export | ❌ | Deliberately off in this release: parallel batches against one database can deadlock on hot pages. |
| Password set | ❌ | Passwords held in a database are the owning application's concern, stored however it chose, so there is no password channel to offer. |

### Schema Discovery

Importing the schema reads the database's catalogue for every table and view the Object Types document names, and presents each Object Type with one attribute per column, typed from the column's declared SQL type. See [Type mapping](#type-mapping) for the full table.

Discovery also:

- **Chooses the External ID for you.**<br /> The anchor column you named becomes the Object Type's recommended External ID attribute. Where an Object Type has a composite anchor (two or more columns), JIM composes a single read-only Text attribute from them, named by joining the column names with `+` (`REGION+EMPLOYEE_NO`), because a Connected System Object is identified by one value.
- **Records what it inferred a type from.**<br /> Every attribute's Description begins `Source column type: NUMBER(10).` (or `nvarchar(100)`, `datetime2`, and so on), so you can see the declaration a type came from before deciding whether to disagree with it. See [Overriding an inferred type](../configuration/connected-systems.md#overriding-an-inferred-type).
- **Marks writability from the source.**<br /> Columns of a table are writable, and its anchor columns are writable on creation only (a primary key is set when the row is inserted and never rewritten). Columns of a view, or of a `select` statement, are read-only, because JIM cannot write through either.
- **Suggests references from foreign keys, but never assumes them.**<br /> Where a table declares a foreign key to another Object Type's anchor column, the attribute's Description says so and shows the `referencesObjectType` line to add. The column stays typed as the database declares it until you configure it as a Reference; a foreign key is a strong hint, but only you know whether JIM should follow it.
- **Skips what it cannot type, and says so.**<br /> A column of a type JIM has no equivalent for (an `xml` or `geography` column, say) is left out with a warning naming it. The same column in a load-bearing position, as an anchor, a configured reference, or a related table's value or join column, fails the discovery instead, because a schema missing one of those would be wrong rather than incomplete.

Object Types and attributes are selected for synchronisation on the Connected System's Schema tab in the usual way; an Object Type in the document that you have not selected is skipped by every Run Profile.

### Full Import

A Full Import reads every row of each selected Object Type's source, a page at a time.

- **Keyset paging, never `OFFSET`.**<br /> Each page is read as `WHERE anchor > last-anchor-of-previous-page ORDER BY anchor`, so the cost of reading page 500 is the same as page 1. This is what makes a 500,000-row table practical, and it is why the anchor columns must be listed in key order in the Object Types document.
- **One query per related table per page.**<br /> Multi-valued attributes are gathered for the whole page in one statement, not one per row. Rows whose join column is `NULL` belong to no parent and are skipped; `NULL` values are skipped.
- **A row count first.**<br /> The first page of each Object Type reports `SELECT COUNT(*)` from its source, so the Activity shows a real percentage and time remaining, and the counters move while a page is still being read.
- **Failures are asymmetric on purpose.**<br /> A value that cannot be converted (text in a column JIM was told holds a number, a fractional value in a whole-number attribute) errors that one object and the import continues. Configuration that cannot work (a source the account cannot see, a `NULL` in an anchor column, a number too wide for JIM to hold exactly) fails the run, because every object would be affected.

!!! note "Whole numbers stay whole"
    A fractional value arriving in an attribute you have recorded as a whole number is refused for that object rather than rounded: `4200.7` silently becoming `4201` is the kind of error nobody finds. Either record the attribute as a Decimal on the Schema tab, or correct the source.

### Delta Import

A database has no change feed of its own, so a Delta Import needs one of two things you provide. The **Delta Import Mode** setting chooses which:

| Mode | How JIM finds changes | Detects |
|------|-----------------------|---------|
| **Change-Log Table** (recommended) | Reads a table or view your database maintains, holding one row per change with the object's anchor, a change type and a monotonic sequence or timestamp. | Creates, updates and **deletions**. |
| **Watermark Column** | Reads a last-modified or version column on each Object Type's own source, and one on each related table, selecting every row that has moved past the last value JIM saw. | Creates and updates only. **A deleted row has no column left to move**, so deletions are found only by a Full Import. |

Each mode has its own end-to-end setup guide, including the table and trigger definitions for both databases:

- [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md)
- [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md)

Whichever mode you choose, JIM keeps a watermark per source (each Object Type, and in Watermark Column mode each related table too), never one maximum across them all. It is captured before a single change is read, and saved only once the run has read its last page, so a run that dies half way through re-reads its changes rather than skipping them. There is no upper bound on a run: a change arriving while the run is in flight is read by this run or the next one, and possibly both, because re-importing an unchanged row costs a comparison whereas an upper bound would silently drop anything committed inside it.

!!! important "The first Delta Import after configuring is a Full Import"
    JIM holds no watermark until an import has completed, so the first Delta Import you run, or the first after changing the Delta Import Mode, performs a Full Import instead and says so on the Activity as a warning. That run establishes the watermark, and the next Delta Import runs normally. Where the mode has never been chosen at all, a Delta Import is refused rather than quietly costing a Full Import every cycle.

### Export

An Export writes Pending Exports to the Object Type's table. Each object is written inside a transaction of its own: its row and every related-table row belonging to it are committed together or rolled back together, because a half-written object is worse than an unwritten one.

| Change | What JIM does |
|--------|---------------|
| Create | Inserts the row, then its related rows. Where the table generates the key (an identity column, a sequence, a default), JIM reads the generated value back and records it as the External ID; where you flow the key yourself (a natural identifier), JIM writes it. |
| Update | Updates only the columns whose values changed. Anchor columns are never rewritten: an update that would touch one is refused with an explanation, because a rewritten primary key would orphan the object without any error. |
| Delete | Deletes the related rows first, then the parent row, without relying on the schema declaring a cascade. A row that has already gone counts as success. |
| Multi-valued add / remove | Inserts or deletes rows of the related table. |
| Reference | Writes the referenced Connected System Object's own identifier, converted to whatever type the column holds. An export whose reference has not yet been provisioned into this database is deferred and retried once it has. |

Every write reads its affected-row count back and answers for it. A statement that raised no error but changed nothing (a trigger discarding the insert, a row deleted outside JIM) fails that object with a message saying what to check, rather than confirming an object the table does not hold. A failure is confined to its object; the batch continues, and the failed export enters JIM's normal retry and backoff.

Because a committed transaction is a verified write, an export needs no confirming import to be trusted: the connector reports **auto-confirm export**.

!!! note "Views and statements are read-only"
    An Object Type whose source is a view or a `select` statement cannot be exported to. Point the Object Type at a table to write to it; import through the view where the view is what the application maintains.

### Type mapping

Every column is typed from its declared SQL type. Microsoft SQL Server's named types state their width exactly and map by name; Oracle has one numeric type, `NUMBER`, so JIM reads its declared precision and scale and picks the narrowest JIM type guaranteed to hold every value the declaration permits.

| JIM type | Microsoft SQL Server | Oracle Database |
|----------|----------------------|-----------------|
| Text | `char`, `nchar`, `varchar`, `nvarchar`, `text`, `ntext` | `CHAR`, `NCHAR`, `VARCHAR2`, `NVARCHAR2`, `CLOB`, `NCLOB`, `LONG` |
| Number | `int`, `smallint`, `tinyint` | `NUMBER(p,0)` with p up to 9 |
| Long Number | `bigint` | `NUMBER(p,0)` with p from 10 to 18 |
| Decimal | `decimal`, `numeric`, `money`, `smallmoney`, `float`, `real` | `NUMBER(p,0)` with p of 19 or more; `NUMBER(p,s)` with a scale; `NUMBER` with no precision; `BINARY_FLOAT`, `BINARY_DOUBLE` |
| Boolean | `bit` | `NUMBER(1)`, only with **Treat NUMBER(1) Columns as Boolean** on |
| Date/Time | `date`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset` | `DATE`, `TIMESTAMP`, `TIMESTAMP WITH TIME ZONE`, `TIMESTAMP WITH LOCAL TIME ZONE` |
| GUID | `uniqueidentifier` | `RAW(16)`, only with **Treat RAW(16) Columns as Guid** on |
| Binary | `binary`, `varbinary`, `image`, `timestamp` / `rowversion` | `RAW`, `LONG RAW`, `BLOB` |
| Reference | any column you configure with `referencesObjectType` | any column you configure with `referencesObjectType` |

Two things about that table are worth knowing before you build on it:

- **Oracle's `NUMBER(10)` is a Long Number, not a Number.** Ten digits already exceed a 32-bit whole number, so the ordinary sequence-backed identifier lands one step wider than you might expect. Where such a column needs to reach a built-in Metaverse Attribute that is a Number (`Employee Number` is the usual case), record it as a Number on the Schema tab; JIM permits the override precisely because Oracle's declaration cannot say. The full inference table and the override procedure are in [Attribute data types](../configuration/connected-systems.md#attribute-data-types).
- **Approximate types round-trip approximately.** `float`, `real`, `BINARY_FLOAT` and `BINARY_DOUBLE` are read as Decimal so that numeric comparison still works, but a binary floating-point value converted to decimal and back is not bit-exact. Prefer an exact numeric column where JIM is authoritative for the value.

A Decimal attribute holds 28 to 29 significant digits. A value wider than that fails the run rather than being rounded, with a message naming the column; narrow the column, stop selecting the attribute, or expose the source through a view that casts it down. Column types with no JIM equivalent (`xml`, `XMLTYPE`, `sql_variant`, `hierarchyid`, `geography`, `geometry`, `INTERVAL`, `time`, `ROWID`, `BFILE`) are skipped by discovery with a warning; expose them through a view that casts to a supported type if they need synchronising.

### Date and time

JIM stores every date and time in UTC. A column that carries its own offset (`datetimeoffset`, `TIMESTAMP WITH TIME ZONE`) needs no interpreting and is converted exactly. A column that carries none (`datetime2`, `DATE`, `TIMESTAMP`) is interpreted in the Connected System's **Database Time Zone** on import, and converted back into that zone on export. The default is UTC, which is the one answer that never silently shifts a value by an hour twice a year; enter an IANA name such as `Europe/London` where the application genuinely records local time.

On Oracle, JIM also pins the session's time zone to the same value when it connects, which is what makes `TIMESTAMP WITH LOCAL TIME ZONE` read consistently. An Oracle Database whose own time zone file does not know the region you named refuses the connection with a message saying so, rather than reading dates in whatever zone the JIM host happens to use.

## Connection Settings

Settings are grouped exactly as they appear on the Connected System's Settings tab. Every setting explains itself in the portal too; this table is the same text, gathered.

### Database Server

| Setting | Required | Description |
|---------|----------|-------------|
| Database Type | Yes | **Microsoft SQL Server** or **Oracle Database**. Decides which details are asked for below, and which dialect JIM speaks. |
| Host | Yes | The database server's hostname or IP address. For a named Microsoft SQL Server instance, use the `SERVER\INSTANCE` form. |
| Port | No | Leave blank for the default: 1433 for Microsoft SQL Server, 1521 for Oracle Database, or 2484 for Oracle Database over TCPS. |
| Database Name | SQL Server | The database to connect to on this server. |
| Oracle Database Identified By | Oracle | **Service Name** (the modern form, and the one to use unless the estate still addresses the database by SID) or **SID**. |
| Oracle Service Name | Oracle, by service name | The service name, for example `HRPROD.example.com`. Oracle Database Free's pluggable database is `FREEPDB1`. |
| Oracle SID | Oracle, by SID | The System Identifier, for example `HRPROD`. |

JIM builds the connection string itself from these values; there is no connection string to paste, and nothing in it is logged.

### Credentials

| Setting | Required | Description |
|---------|----------|-------------|
| Username | Yes | The database account JIM connects as. Give it the least privilege the Run Profiles need: read-only on an import-only Connected System. See [Database account permissions](#database-account-permissions). |
| Password | Yes | Stored encrypted, decrypted only to hand to the driver, and never written to a log or a configuration snapshot. |

A SQL Server connection identifies itself with the application name `JIM`, so a DBA can attribute sessions and locks to it. Connections are not pooled on either database: nothing stays open on your database between runs.

### Transport Security

| Setting | Applies to | Default | Description |
|---------|------------|---------|-------------|
| Encrypt Connection | SQL Server | On | Encrypt the connection with TLS. When on, the connection **fails** rather than falling back to plain text if the server cannot encrypt. The server's certificate is always validated, against the operating system's certificate bundle and any certificates added in **Admin > Certificates**; JIM never trusts whatever certificate the server happens to present. |
| Oracle Encryption | Oracle | Native Network Encryption | **Native Network Encryption** encrypts the session on the ordinary listener with no certificate at either end (AES, with SHA-2 integrity checking, both required rather than negotiable), and is how Oracle estates usually encrypt client traffic. **TCPS (TLS)** needs a separately configured listener, usually on port 2484, and a server certificate. **None** sends the session in clear and should be reserved for a lab. |
| Connection Timeout | Both | 30 seconds | How long to wait before giving up on connecting. |

#### When a connection test fails on the certificate

For SQL Server, a server certificate JIM does not yet trust (an internal certificate authority, or a self-signed certificate) fails the connection test, and JIM shows you the certificate the server presented: its subject, the names it was issued for, its issuer, validity dates and thumbprint, and which check it failed. Add the issuing authority (or the certificate itself, for a self-signed one) under **Admin > Certificates** and test again.

Trust is strictly additive. JIM connects with the operating system's trust anchors first, and only when the driver refuses does it look at the certificate the server presented; only a certificate that JIM's own store vouches for, and that passes JIM's own checks of validity period and name, is then handed to the driver as an additional anchor. An expired certificate, or one issued for a different name from the one you connect to, is still refused, and JIM says which.

For Oracle over TCPS the same certificate detail is shown on failure, but the ODP.NET managed driver offers no way for JIM to add a trust anchor of its own: a TCPS certificate must be one the operating system on the JIM host already vouches for. Native Network Encryption is unaffected, using no certificate at all, which is one reason it is the default.

### Date and Time

| Setting | Default | Description |
|---------|---------|-------------|
| Database Time Zone | `UTC` | The time zone that date and time columns carrying no offset are recorded in. See [Date and time](#date-and-time). Enter `UTC`, or an IANA time zone name such as `Europe/London`. A name this deployment does not recognise is refused when you save. |

### Type Mapping

| Setting | Applies to | Default | Description |
|---------|------------|---------|-------------|
| Treat NUMBER(1) Columns as Boolean | Oracle | Off | Oracle has no boolean column type, so flags are usually held as `NUMBER(1)`. Turn this on if that is what `NUMBER(1)` columns mean in this database; leave it off and they import as numbers. It applies to every `NUMBER(1)` column in the schema, so only turn it on where that is true of all of them. |
| Treat RAW(16) Columns as Guid | Oracle | Off | Oracle holds GUIDs in `RAW(16)` columns, but so it does digests and other binary values, and the catalogue cannot tell them apart. Turn this on if `RAW(16)` columns in this database hold GUIDs; leave it off and they import as binary values. |

Both are opt-ins because a wrong guess in either direction would be a silent one. Where a database mixes the two meanings, leave the setting off: the columns import as Numbers or as binary values, and the ones that are really flags or GUIDs can be converted in the Attribute Flow, or exposed through a view under a type that says what they are.

### Object Type Configuration

| Setting | Required | Description |
|---------|----------|-------------|
| Object Types | Yes | The JSON document naming each Object Type this database holds and where its objects come from. See [The Object Types document](#the-object-types-document). |

### Delta Import

| Setting | Required | Description |
|---------|----------|-------------|
| Delta Import Mode | No | **Change-Log Table** or **Watermark Column**. Leave it unanswered where this Connected System only runs Full Imports; JIM will not default it, because that would have it read changes from a table nobody has said exists. See [Delta Import](#delta-import). |

## The Object Types document

The **Object Types** setting is a JSON document with one entry per Object Type. It is validated when you save the Connected System, strictly: an unknown field name is an error rather than ignored, because a typo that parses is a defect that only surfaces as missing data much later. Every refusal names the Object Type and the field.

```json title="A person with a manager reference and a related table of phone numbers"
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

### Object Type fields

| Field | Required | Meaning |
|-------|----------|---------|
| `name` | Yes | What JIM calls the Object Type. Must be unique within the document. A standard name (`User`, `Group`, `Person`) lets JIM map it to a Metaverse Object Type automatically. |
| `table` | One of `table` / `select` | The table **or view** objects are read from. |
| `schema` | No | Qualifies `table`. Leave it out and JIM resolves the name from the catalogue; a least-privilege account usually sees exactly one object of a given name. Where the name exists in more than one schema JIM asks you to say which. Not allowed with `select`. |
| `select` | One of `table` / `select` | A `SELECT` statement standing in for a table or view, for the cases where a view cannot be created. Must begin `SELECT` or `WITH`, must be a single statement with no terminator, and cannot be exported to. JIM asks the database to plan it at discovery without reading a row, so a statement the database would not accept is refused when you save. |
| `anchorColumns` | Yes | The column or columns whose values identify a row, **in key order**. The order is what makes a keyset page boundary reproducible between runs, so JIM never sorts it. |
| `columns` | No | Per-column configuration; today that means naming the Object Type a column's values point at, so JIM resolves it as a Reference. Each entry is `{ "name": ..., "referencesObjectType": ... }`, and the referenced Object Type must be declared in the same document. |
| `relatedTables` | No | Multi-valued attributes, one entry per attribute. See below. |
| `watermarkColumn` | Watermark Column mode | The last-modified or version column on this Object Type's own source. See [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md). |
| `changeLog` | Change-Log Table mode | The change-log table for this Object Type. See [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md). |

### Related table fields

A related table turns a table of one row per value into a multi-valued attribute on the parent. Group membership is exactly this shape: a `GROUP_MEMBERS` table of `(GROUP_ID, MEMBER_ID)` becomes a `Members` attribute on the `Group` Object Type, and with `referencesObjectType` its values are References to `Person` objects.

| Field | Required | Meaning |
|-------|----------|---------|
| `attributeName` | Yes | What the multi-valued attribute is called on the Object Type. Named by you because a value column's own name (`PHONE_NUMBER`) rarely reads as the plural attribute it becomes. Must not clash with a column of the parent. |
| `table` / `schema` | Yes / No | The related table, qualified as for an Object Type. |
| `valueColumn` | Yes | The column holding the value. Its SQL type decides the attribute's type. |
| `joinColumns` | Yes | The columns that join a row back to its parent: **one per anchor column, in the same order**. Joining on part of an anchor would gather another object's values onto this one, so JIM refuses a mismatch. |
| `referencesObjectType` | No | Where the values are References, the Object Type they point at. |
| `watermarkColumn` | Watermark Column mode | The column that moves when a row of this table changes. |

## Worked examples

### Microsoft SQL Server: an HR application

`dbo.Employees` is keyed on an `IDENTITY` column, holds a self-referencing `ManagerId`, and has a child table of phone numbers. Departments live in their own table and are referenced by key.

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

What you get: `EmployeeId` (`int`) is the External ID and is set on creation only; `ManagerId` and `DepartmentId` are References; `PhoneNumbers` is a multi-valued Text attribute; `HireDate` (`datetime2`) is interpreted in the Database Time Zone; `IsActive` (`bit`) is a Boolean. Exporting a new `Person` inserts the row without `EmployeeId`, reads the identity value back and records it as the External ID, then inserts its phone numbers.

### Oracle Database: an HR view over a sequence-keyed table

The application exposes `HR.V_EMPLOYEES` for reading and expects writes to go to `HR.EMPLOYEES`, whose `EMPLOYEE_ID NUMBER(10)` is filled by a sequence-backed default. Group membership is a related table of References.

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

What you get: `EMPLOYEE_ID` discovers as a Long Number (`NUMBER(10)`); record it as a Number on the Schema tab if it is to flow to the built-in `Employee Number`. `IS_ACTIVE NUMBER(1)` is a Number unless **Treat NUMBER(1) Columns as Boolean** is on. `HIRE_DATE` (`DATE`) is interpreted in the Database Time Zone. `Members` is a multi-valued Reference to `Person`; exporting a `Group` inserts its row and one `GROUP_MEMBERS` row per member, in one transaction.

!!! tip "Read through the view, write to the table"
    Where the application maintains a view for readers, you can point an import-only Connected System at the view and a second Connected System at the table for writes, or point one Connected System at the table for both. A single Object Type cannot read from the view and write to the table.

## Security Considerations

### Trust model

The Object Types document, including any `select` statement, is privileged administrator input: it names the tables JIM reads and writes, exactly as a Synchronisation Rule decides what flows where. Everything JIM binds into a statement is bound as a parameter, never interpolated; identifiers cannot be parameterised, so JIM quotes and validates every one it uses. Those two are the injection surface, and neither depends on the document being trusted.

### Database account permissions

Create a dedicated database account for JIM and grant it only what the Run Profiles you configure will use.

- **For schema discovery and import**<br /> `SELECT` on each table or view named in the Object Types document, on each related table, and (in Change-Log Table mode) on each change-log table. JIM reads the catalogue through `INFORMATION_SCHEMA` and `sys` views on SQL Server and the `ALL_*` views on Oracle, which show exactly the objects the account can already read; nothing wider is needed.
- **For export**<br /> `INSERT`, `UPDATE` and `DELETE` on each Object Type's table and each related table, alongside `SELECT`, which the export uses to read the column catalogue and check its writes.
- **Nothing else.**<br /> JIM creates no objects, alters no schema, and never needs `db_owner`, `DBA`, or `SELECT ANY TABLE`.

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

Grant per object on Oracle rather than `SELECT ANY TABLE`; the `ALL_*` catalogue views then show JIM exactly, and only, what it should see, and a discovery error saying an object "cannot be seen" is a permission you have not granted rather than a mystery.

### Encryption in transit

- **SQL Server** connections are encrypted by default and fail rather than fall back to plain text. `TrustServerCertificate` is never set: the certificate is always validated.
- **Oracle** connections use Native Network Encryption by default (AES with SHA-2 integrity checking, both required), or TCPS where the listener offers it. Choose **None** only in a lab.
- Neither database's driver needs anything installed on the JIM host beyond JIM itself, and neither connection reaches anything but the host you name, so the connector works unchanged in an air-gapped deployment.

### Credentials and logs

The password is stored encrypted, decrypted only to hand to the driver's connection string builder, and appears in no log, Activity, or configuration snapshot. The driver's own error messages never carry it either.

## Third-party licence notice

The Oracle dialect is provided through **Oracle Data Provider for .NET, Managed Driver** (`Oracle.ManagedDataAccess.Core`), which Oracle distributes under the **Oracle Free Distribution, Hosting, and Use Terms and Conditions**. Those terms permit the driver to be redistributed unmodified and used in your business operations at no charge, on conditions that include a copy of the terms accompanying any distribution and Oracle's proprietary notices being left in place. JIM ships a copy of the terms in every image, under `/app/third-party-notices/`, and in the repository under [`third-party-notices/`](https://github.com/TetronIO/JIM/tree/main/third-party-notices). If your organisation has its own licence agreement with Oracle covering the driver, that agreement governs your use of it instead. Nothing here is legal advice; read the terms.

The Microsoft SQL Server dialect is provided through `Microsoft.Data.SqlClient`, which is MIT licensed.

## Wider database support

The connector is built as one connector with a dialect per database, so a further database is an addition behind the same settings and the same Object Types document rather than a new connector. PostgreSQL and MySQL are next on the [roadmap](../reference/roadmap.md). If you need another engine, or need one of those sooner, say so in the [Ideas category of GitHub Discussions](https://github.com/TetronIO/JIM/discussions/categories/ideas); which dialect comes next is decided by who asks.

## Troubleshooting

**"Unable to connect. Message: ..."**<br />
The driver's own message follows. A wrong host or a closed port times out after the Connection Timeout; a wrong Database Name, service name or SID is refused immediately with the database's own wording; a wrong password is an authentication failure. On Oracle, check that you have chosen the right form (service name or SID) as well as the right value.

**The connection test fails on the certificate (SQL Server)**<br />
JIM shows the certificate the server presented and which check failed. An untrusted issuer is fixed by adding the authority under **Admin > Certificates**; an expired certificate has to be renewed on the server; a name mismatch means connecting by a name the certificate carries. See [When a connection test fails on the certificate](#when-a-connection-test-fails-on-the-certificate).

**"Object Type 'X' reads from HR.TABLE, which this database account cannot see"**<br />
Either the name is wrong, or the schema is, or the account has not been granted `SELECT` on it. On Oracle a grant is per object; see [Database account permissions](#database-account-permissions).

**"Object Type 'X' names 'TABLE', which exists in more than one schema"**<br />
Add a `schema` to the Object Type to say which one.

**An attribute is missing after schema discovery**<br />
Check the discovery's warnings: a column of a type JIM has no equivalent for is skipped and named. Expose it through a view that casts it to a supported type.

**A whole-number attribute reports "the value has a fractional part"**<br />
The column is fractional but the attribute was recorded as a Number or Long Number, usually through an override. Record it as a Decimal on the Schema tab, or correct the source.

**A Delta Import performed a Full Import instead**<br />
Expected the first time, and after a change of Delta Import Mode: no usable watermark existed, so the run established one. If it happens on every run, check the Activity's warning for the reason.

**A Delta Import never sees deletions**<br />
Watermark Column mode cannot; a deleted row has no column left to move. Use a change-log table, or schedule a periodic Full Import. See [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md).

**An export reports "No row was written ... though the database raised no error"**<br />
A trigger or a rule on the table is discarding the insert, or the account may not write to it. JIM rolls the object back rather than confirming a row the table does not hold; check the table's triggers and the account's grants.

**An export reports "No row ... is identified by this Connected System Object's external ID"**<br />
The row was deleted outside JIM, or its key was changed. Run a Full Import to reconcile the Connected System with the table.

**An Oracle export fails with "returned no value for its anchor column"**<br />
The table's key is neither flowed by a Synchronisation Rule nor generated by the database. Back the column with a sequence-based default or an identity column, or flow the key.

**Dates are an hour out**<br />
The column carries no offset and the Database Time Zone does not match what the application writes. Set it to the zone the application records in; columns with their own offset are unaffected either way.

## Related

- [Delta Import with a change-log table](jim-sql-connector-delta-import-change-log.md)
- [Delta Import with a watermark column](jim-sql-connector-delta-import-watermark.md)
- [Attribute data types](../configuration/connected-systems.md#attribute-data-types)
- [Connectors](index.md)
- [Connected Systems](../configuration/connected-systems.md)
