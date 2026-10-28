---
title: SQL Connector Worked Examples
description: Two complete SQL Connector configurations, an HR application on Microsoft SQL Server and an HR schema with groups on Oracle Database.
type: reference
audience: administrator
---

# SQL Connector Worked Examples

Two complete configurations for the [SQL Connector](jim-sql-connector.md), each with the Object Types document that describes its tables. Start from the one closest to your database.

```jim-diagram
archetype: comparison
title: The two worked examples compared
desc: "The Microsoft SQL Server example is an HR application with Person and Department Object Types; a Person's ManagerId references another Person and its DepartmentId a Department. Its key, EmployeeId, is an int identity column, which JIM reads back as the External ID when it creates a row. Its multi-valued attribute is PhoneNumbers, Text values from the dbo.EmployeePhones table. The Oracle Database example is an HR schema with Person and Group Object Types; a Person's MANAGER_ID references another Person. Its key, EMPLOYEE_ID, is a sequence-backed NUMBER(10), which imports as a Long Number. Its multi-valued attribute is Members, references to Person from the HR.GROUP_MEMBERS table. Exporting a new Person in the first inserts the row, reads the identity value back, then inserts the phone numbers; exporting a Group in the second inserts its row and one GROUP_MEMBERS row per member. Either way, an object's row and its related-table rows are written in one transaction."
columns:
  - { label: Microsoft SQL Server, sub: an HR application }
  - { label: Oracle Database, sub: an HR schema with groups }
rows:
  - label: Object Types
    cells:
      - { text: "Person, Department", sub: "a manager and a department as references" }
      - { text: "Person, Group", sub: a manager as a reference }
  - label: Key
    cells:
      - { text: "`EmployeeId`, an identity", sub: read back as the External ID }
      - { text: "`EMPLOYEE_ID NUMBER(10)`", sub: "sequence-backed; a Long Number" }
  - label: Related table
    cells:
      - { text: "`PhoneNumbers`", sub: "Text values, from `dbo.EmployeePhones`" }
      - { text: "`Members`", sub: "references to Person, from `HR.GROUP_MEMBERS`" }
  - label: Exporting a new object
    cells:
      - { text: "a Person: inserts the row", sub: "reads the identity back, then inserts the phone numbers" }
      - { text: "a Group: inserts the row", sub: "then one `GROUP_MEMBERS` row per member" }
footer: "Either way, an object's row and its related-table rows are written in one transaction"
```

## :material-microsoft: Microsoft SQL Server: an HR application

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

## :material-database-outline: Oracle Database: an HR schema with groups

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

## :material-book-open-variant-outline: Related

- [JIM SQL Connector](jim-sql-connector.md): describing your tables, and every setting.
