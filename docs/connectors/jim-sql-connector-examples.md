---
title: SQL Connector Worked Examples
description: Two complete SQL Connector configurations, an HR application on Microsoft SQL Server and an HR schema with groups on Oracle Database.
type: reference
audience: administrator
---

# SQL Connector Worked Examples

Two complete configurations for the [SQL Connector](jim-sql-connector.md), each with the Object Types document that describes its tables. Start from the one closest to your database.

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
