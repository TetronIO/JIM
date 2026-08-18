# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Seed a deterministic HR schema into a Scenario 16 database container

.DESCRIPTION
    Builds the schema the JIM SQL Connector matrix (Scenario 16) runs against, in one database server,
    with a caller-chosen row count. The Generate-TestCSV.ps1 model applies: the same inputs always
    produce the same database, and a content hash of the generated script lets an unchanged seed be
    skipped rather than rebuilt.

    Every value is derived arithmetically from the row number, so the data is identical at 50 rows and
    at 500,000, and an assertion written against row 7 holds at every scale. Rows are generated
    set-based (one INSERT ... SELECT over a generated series) rather than as one statement per row:
    500,000 individual INSERTs would take hours through sqlcmd or SQL*Plus, and the scale requirement
    is the whole reason the row count is a parameter.

    The schema deliberately includes the column shapes the connector's unit tests can only assume:
    an Oracle RAW(16) primary key defaulted from SYS_GUID(), a zoneless and an offset-carrying date and
    time column side by side, Oracle's TIMESTAMP WITH LOCAL TIME ZONE, and NUMBER columns of several
    declared precisions. See the matrix scenario for what each is asserted against.

.PARAMETER Provider
    Which database server to seed (SqlServer or Oracle).

.PARAMETER RowCount
    How many employee rows to generate. The default suits the functional rows of the matrix; the scale
    row passes 500000.

.PARAMETER Force
    Re-seed even when the content hash says the database already matches.

.EXAMPLE
    ./New-Scenario16TestDatabase.ps1 -Provider SqlServer
    ./New-Scenario16TestDatabase.ps1 -Provider Oracle -RowCount 500000
#>

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("SqlServer", "Oracle")]
    [string]$Provider,

    [Parameter(Mandatory=$false)]
    [ValidateRange(1, 5000000)]
    [int]$RowCount = 50,

    [Parameter(Mandatory=$false)]
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/utils/Test-Helpers.ps1"

$config = Get-DatabaseConfig -Provider $Provider

# Fixed points the generated data is derived from. Absolute rather than relative to "now" so that a
# database seeded today and one seeded next month are byte-for-byte the same, which is what makes the
# content hash meaningful and what keeps an assertion on a specific date from rotting.
$baseDate = "2020-01-06"

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# Script generation
# ─────────────────────────────────────────────────────────────────────────────────────────────────

function New-SqlServerSourceRowInserts {
    <#
    .SYNOPSIS
        The INSERTs that author the source rows, shared by the full seed and the hash-match reset.
    .DESCRIPTION
        One authoring, two consumers, so the reset can never drift from the seed: the matrix's rows
        mutate the source tables (Export.Update rewrites an email and adds a phone number,
        Export.Delete disables employee 20), and the reset has to put back exactly what the seed
        would have written or the next run inherits the mutations as residue.
    #>
    param([int]$Rows)

    return @"
-- Set-based generation. sys.all_objects cross-joined with itself supplies far more rows than the
-- 500,000 ceiling, and ROW_NUMBER turns them into the series every value is derived from.
WITH Numbers AS (
    SELECT TOP ($Rows) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO hr.EMPLOYEES
    (EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT, MANAGER_EMPLOYEE_ID,
     HEADCOUNT, FTE, IS_ACTIVE, START_DATE, LAST_MODIFIED, HIRED_AT, EMPLOYEE_GUID, PHOTO)
SELECT
    n,
    CONCAT('$($config.EmployeeNumberPrefix)', RIGHT(CONCAT('00000000', CAST(n AS varchar(20))), 8)),
    CHOOSE((n % 8) + 1, 'Ada', 'Bram', 'Cleo', 'Dara', 'Emil', 'Fern', 'Gita', 'Hugo'),
    CHOOSE((n % 6) + 1, 'Ashcroft', 'Brandt', 'Calder', 'Duquesne', 'Ellery', 'Fairhurst'),
    CONCAT('user', CAST(n AS varchar(20)), '@panoply.local'),
    CHOOSE((n % 4) + 1, 'Engineering', 'Finance', 'Operations', 'Research'),
    -- The first ten rows have no manager, so a reference that is legitimately absent is covered too.
    CASE WHEN n > 10 THEN ((n % 10) + 1) ELSE NULL END,
    CAST(n AS bigint) * 1000000000,
    -- Two decimal places that binary floating point cannot represent exactly, which is the point.
    CAST(0.25 + ((n % 4) * 0.25) AS $($config.Types.Decimal)),
    CASE WHEN (n % 7) = 0 THEN 0 ELSE 1 END,
    DATEADD(day, n, CAST('$baseDate' AS datetime2(3))),
    DATEADD(minute, n, CAST('$baseDate' AS datetime2(3))),
    -- A non-UTC stored offset, so normalisation to UTC is observable rather than a no-op.
    TODATETIMEOFFSET(DATEADD(minute, n, CAST('$baseDate' AS datetime2(3))), '-05:00'),
    -- Deterministic, not NEWID(): a re-seed must produce the same identifiers.
    CAST(CAST(RIGHT(CONCAT('00000000', CAST(n AS varchar(20))), 8) + '-0000-4000-8000-000000000000' AS char(36)) AS uniqueidentifier),
    CAST(n AS varbinary(64))
FROM Numbers;
GO

-- Two phone numbers for every third employee, and one for the rest, so the multi-valued assertions
-- have both cardinalities to work with.
INSERT INTO hr.EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER, LAST_MODIFIED)
SELECT EMPLOYEE_ID, CONCAT('+44 20 7000 ', RIGHT(CONCAT('0000', CAST(EMPLOYEE_ID AS varchar(20))), 4)), LAST_MODIFIED
FROM hr.EMPLOYEES;
GO

INSERT INTO hr.EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER, LAST_MODIFIED)
SELECT EMPLOYEE_ID, CONCAT('+44 161 496 ', RIGHT(CONCAT('0000', CAST(EMPLOYEE_ID AS varchar(20))), 4)), LAST_MODIFIED
FROM hr.EMPLOYEES WHERE (EMPLOYEE_ID % 3) = 0;
GO

-- The change logs are not seeded by hand: the triggers write one creation entry per employee (and the
-- phone rows' update entries) as the rows above go in, so a Delta Import from a zero watermark sees the
-- whole population and the log holds exactly what the triggers write.
"@
}

function New-SqlServerSeedScript {
    param([int]$Rows)

    # GO batches matter: sqlcmd sends everything up to a GO as one batch, and CREATE VIEW must be the
    # only statement in its own batch.
    return @"
SET NOCOUNT ON;
GO

IF DB_ID('JIMTEST') IS NULL
    CREATE DATABASE JIMTEST;
GO

USE JIMTEST;
GO

-- Drop in dependency order so a re-seed is clean rather than additive.
DROP TABLE IF EXISTS hr.APP_USER_ROLES;
DROP TABLE IF EXISTS hr.APP_USERS_CHANGE_LOG;
DROP TABLE IF EXISTS hr.APP_USERS;
DROP TABLE IF EXISTS hr.APP_ACCOUNTS_CHANGE_LOG;
DROP TABLE IF EXISTS hr.APP_ACCOUNTS_NATURAL;
DROP TABLE IF EXISTS hr.V_EMPLOYEES_CHANGE_LOG;
DROP TABLE IF EXISTS hr.IDM_CHANGE_LOG;
DROP TABLE IF EXISTS hr.EMPLOYEE_PHONES;
DROP VIEW  IF EXISTS hr.V_EMPLOYEES;
DROP TABLE IF EXISTS hr.EMPLOYEES;
DROP TABLE IF EXISTS hr.JIM_SEED_MANIFEST;
GO

IF SCHEMA_ID('hr') IS NULL EXEC('CREATE SCHEMA hr');
GO

-- The import source. EMPLOYEE_ID is a natural, deterministic anchor rather than an identity, so that
-- keyset paging resumes from a value the test can predict; generated keys are exercised separately by
-- the export tables below.
CREATE TABLE hr.EMPLOYEES (
    EMPLOYEE_ID          $($config.Types.Integer)      NOT NULL PRIMARY KEY,
    EMPLOYEE_NUMBER      $($config.Types.NaturalAnchor) NOT NULL,
    FIRST_NAME           $($config.Types.Text)         NOT NULL,
    LAST_NAME            $($config.Types.Text)         NOT NULL,
    EMAIL                $($config.Types.Text)         NOT NULL,
    DEPARTMENT           $($config.Types.Text)         NOT NULL,
    MANAGER_EMPLOYEE_ID  $($config.Types.Integer)      NULL,
    HEADCOUNT            $($config.Types.BigInteger)   NOT NULL,
    FTE                  $($config.Types.Decimal)      NOT NULL,
    IS_ACTIVE            $($config.Types.Boolean)      NOT NULL,
    -- Zoneless: the Connected System's Database Time Zone decides what instant this names.
    START_DATE           $($config.Types.ZonelessDate) NOT NULL,
    -- The Watermark Column mode's column, defaulted on insert and moved by a trigger on update, exactly
    -- as docs/connectors/jim-sql-connector-delta-import-watermark.md prescribes.
    LAST_MODIFIED        $($config.Types.ZonelessDate) NOT NULL DEFAULT SYSUTCDATETIME(),
    -- Offset-carrying: unambiguous at the wire level, so no setting applies to it.
    HIRED_AT             $($config.Types.OffsetDate)   NOT NULL,
    EMPLOYEE_GUID        $($config.Types.Guid)         NOT NULL,
    PHOTO                $($config.Types.Binary)       NULL,
    -- SQL Server's own version stamp: unique and increasing across the database, maintained without a
    -- trigger, and discovered by JIM as a Binary attribute. The Delta.RowversionWatermark row names it
    -- as the watermark column to prove the Binary watermark round-trips.
    ROW_VERSION          rowversion                    NOT NULL
);
GO

-- A view over the same rows, so the matrix can prove a view-backed Object Type imports identically to
-- a table-backed one (and that its anchor stays read-only, views being unwritable).
CREATE VIEW hr.V_EMPLOYEES AS
    SELECT EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT,
           MANAGER_EMPLOYEE_ID, FTE, IS_ACTIVE, START_DATE, LAST_MODIFIED, HIRED_AT, EMPLOYEE_GUID
    FROM hr.EMPLOYEES;
GO

-- Multi-valued attributes come from a related table joined on the parent anchor.
CREATE TABLE hr.EMPLOYEE_PHONES (
    PHONE_ID       $($config.Types.Anchor)       NOT NULL PRIMARY KEY,
    EMPLOYEE_ID    $($config.Types.Integer)      NOT NULL,
    PHONE_NUMBER   $($config.Types.Text)         NOT NULL,
    LAST_MODIFIED  $($config.Types.ZonelessDate) NOT NULL DEFAULT SYSUTCDATETIME(),
    ROW_VERSION    rowversion                    NOT NULL,
    CONSTRAINT FK_PHONES_EMPLOYEE FOREIGN KEY (EMPLOYEE_ID) REFERENCES hr.EMPLOYEES (EMPLOYEE_ID)
);
GO

-- The change-log Delta Import mode's source: the only mode that observes a deletion. The shape is the
-- one docs/connectors/jim-sql-connector-delta-import-change-log.md prescribes: an identity sequence as
-- the watermark, the anchor, a change type, and a timestamp JIM ignores.
CREATE TABLE hr.IDM_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Anchor)       NOT NULL PRIMARY KEY,
    EMPLOYEE_ID  $($config.Types.Integer)      NOT NULL,
    CHANGE_TYPE  nchar(1)                      NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- The view's own change log. Keyed on EMAIL because PersonView is anchored on EMAIL; see the anchor
-- comment in Setup-Scenario16.ps1 for why the view cannot be anchored on EMPLOYEE_ID. Written by the
-- same triggers as IDM_CHANGE_LOG, logging the anchor as the view exposes it, which is what the guide
-- says to do for a view-backed Object Type.
CREATE TABLE hr.V_EMPLOYEES_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Anchor)       NOT NULL PRIMARY KEY,
    EMAIL        $($config.Types.Text)         NOT NULL,
    CHANGE_TYPE  nchar(1)                      NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- The triggers the two Delta Import guides prescribe, verbatim in shape: the change-log triggers write
-- one row per changed employee (a related-table change is an update to its parent), and the
-- last-modified triggers move the watermark column on update. Created before the rows are seeded, so
-- the seeded population's creation entries come from the trigger rather than from a hand-written
-- insert that could drift from what the trigger writes.
CREATE TRIGGER hr.TR_EMPLOYEES_IDM_CHANGE_LOG
ON hr.EMPLOYEES
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    -- Inserts and updates: rows in "inserted"; an update also has the row in "deleted".
    INSERT INTO hr.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    SELECT i.EMPLOYEE_ID, CASE WHEN d.EMPLOYEE_ID IS NULL THEN 'I' ELSE 'U' END
    FROM inserted AS i
    LEFT JOIN deleted AS d ON d.EMPLOYEE_ID = i.EMPLOYEE_ID;

    INSERT INTO hr.V_EMPLOYEES_CHANGE_LOG (EMAIL, CHANGE_TYPE)
    SELECT i.EMAIL, CASE WHEN d.EMPLOYEE_ID IS NULL THEN 'I' ELSE 'U' END
    FROM inserted AS i
    LEFT JOIN deleted AS d ON d.EMPLOYEE_ID = i.EMPLOYEE_ID;

    -- Deletes: rows in "deleted" with no counterpart in "inserted".
    INSERT INTO hr.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    SELECT d.EMPLOYEE_ID, 'D'
    FROM deleted AS d
    LEFT JOIN inserted AS i ON i.EMPLOYEE_ID = d.EMPLOYEE_ID
    WHERE i.EMPLOYEE_ID IS NULL;

    INSERT INTO hr.V_EMPLOYEES_CHANGE_LOG (EMAIL, CHANGE_TYPE)
    SELECT d.EMAIL, 'D'
    FROM deleted AS d
    LEFT JOIN inserted AS i ON i.EMPLOYEE_ID = d.EMPLOYEE_ID
    WHERE i.EMPLOYEE_ID IS NULL;
END;
GO

-- A related table's change is a change to its parent.
CREATE TRIGGER hr.TR_EMPLOYEE_PHONES_IDM_CHANGE_LOG
ON hr.EMPLOYEE_PHONES
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO hr.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    SELECT EMPLOYEE_ID, 'U' FROM inserted
    UNION
    SELECT EMPLOYEE_ID, 'U' FROM deleted;
END;
GO

CREATE TRIGGER hr.TR_EMPLOYEES_LAST_MODIFIED
ON hr.EMPLOYEES
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE e SET LAST_MODIFIED = SYSUTCDATETIME()
    FROM hr.EMPLOYEES AS e
    INNER JOIN inserted AS i ON i.EMPLOYEE_ID = e.EMPLOYEE_ID;
END;
GO

CREATE TRIGGER hr.TR_EMPLOYEE_PHONES_LAST_MODIFIED
ON hr.EMPLOYEE_PHONES
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE p SET LAST_MODIFIED = SYSUTCDATETIME()
    FROM hr.EMPLOYEE_PHONES AS p
    INNER JOIN inserted AS i ON i.PHONE_ID = p.PHONE_ID;
END;
GO

-- Export target with a database-generated key, returned as the external ID via OUTPUT INSERTED.
-- The IDENTITY starts at 1,000,000 rather than 1 so that generated keys never overlap the seeded
-- EMPLOYEE_ID range; see the anchor comment in Setup-Scenario16.ps1. It is still the database that
-- authors the key, which is all the Export.Create row asserts.
CREATE TABLE hr.APP_USERS (
    ID            int IDENTITY(1000000,1)       NOT NULL PRIMARY KEY,
    USER_NAME     $($config.Types.Text)         NOT NULL,
    DISPLAY_NAME  $($config.Types.Text)         NULL,
    EMAIL         $($config.Types.Text)         NULL,
    MANAGER_ID    $($config.Types.Integer)      NULL,
    FTE           $($config.Types.Decimal)      NULL,
    IS_ENABLED    $($config.Types.Boolean)      NOT NULL,
    STARTS_ON     $($config.Types.ZonelessDate) NULL,
    STARTS_AT     $($config.Types.OffsetDate)   NULL,
    -- Every table in the shared Object Types document carries a watermark column, for the same reason
    -- every Object Type carries a change log (below): Watermark Column mode is refused at save time
    -- unless every Object Type in the document names one, whether or not the Connected System has
    -- selected it. Nothing here writes to it beyond the default; the delta rows drive the identity
    -- tables, and these exist so the mode can be declared at all.
    LAST_MODIFIED $($config.Types.ZonelessDate) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE hr.APP_USER_ROLES (
    ROLE_ID        $($config.Types.Anchor)       NOT NULL PRIMARY KEY,
    USER_ID        $($config.Types.Integer)      NOT NULL,
    ROLE_NAME      $($config.Types.Text)         NOT NULL,
    LAST_MODIFIED  $($config.Types.ZonelessDate) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_ROLES_USER FOREIGN KEY (USER_ID) REFERENCES hr.APP_USERS (ID) ON DELETE CASCADE
);
GO

-- Export target with a natural primary key, which JIM must author itself (WritableOnCreate).
CREATE TABLE hr.APP_ACCOUNTS_NATURAL (
    ACCOUNT_CODE  $($config.Types.NaturalAnchor) NOT NULL PRIMARY KEY,
    DISPLAY_NAME  $($config.Types.Text)          NULL,
    IS_ENABLED    $($config.Types.Boolean)       NOT NULL,
    LAST_MODIFIED $($config.Types.ZonelessDate)  NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- A change log per export-target Object Type. Change-Log Table mode is refused at save time unless
-- EVERY selected Object Type has one, and deliberately so: a Delta Import that silently skipped a type
-- would report success while leaving its objects to drift. These stay empty, which is the honest state
-- of affairs, because nothing outside JIM ever writes to the export targets; their existence is what
-- lets the Connected System declare a delta mode at all.
CREATE TABLE hr.APP_USERS_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Anchor)       NOT NULL PRIMARY KEY,
    ID           $($config.Types.Integer)      NOT NULL,
    CHANGE_TYPE  nchar(1)                      NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) NOT NULL
);
GO

CREATE TABLE hr.APP_ACCOUNTS_CHANGE_LOG (
    CHANGE_ID     $($config.Types.Anchor)        NOT NULL PRIMARY KEY,
    ACCOUNT_CODE  $($config.Types.NaturalAnchor) NOT NULL,
    CHANGE_TYPE   nchar(1)                       NOT NULL,
    CHANGED_AT    $($config.Types.ZonelessDate)  NOT NULL
);
GO

CREATE TABLE hr.JIM_SEED_MANIFEST (
    SEED_HASH  nvarchar(64) NOT NULL PRIMARY KEY,
    ROW_COUNT  int          NOT NULL,
    SEEDED_AT  datetime2(3) NOT NULL
);
GO

$(New-SqlServerSourceRowInserts -Rows $Rows)

INSERT INTO hr.JIM_SEED_MANIFEST (SEED_HASH, ROW_COUNT, SEEDED_AT)
VALUES ('{0}', $Rows, SYSUTCDATETIME());
GO

SELECT 'SEEDED' AS Result, COUNT(*) AS Employees FROM hr.EMPLOYEES;
GO
"@
}

function New-OracleSourceRowInserts {
    <#
    .SYNOPSIS
        The Oracle sibling of New-SqlServerSourceRowInserts; same one-authoring rationale.
    #>
    param([int]$Rows)

    return @"
-- Set-based generation. CONNECT BY LEVEL against DUAL is Oracle's generated series.
INSERT INTO EMPLOYEES
    (EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT, MANAGER_EMPLOYEE_ID,
     HEADCOUNT, FTE, IS_ACTIVE, START_DATE, LAST_MODIFIED, HIRED_AT, HIRED_AT_LOCAL, EMPLOYEE_GUID, PHOTO)
SELECT
    n,
    '$($config.EmployeeNumberPrefix)' || LPAD(TO_CHAR(n), 8, '0'),
    DECODE(MOD(n, 8), 0, 'Ada', 1, 'Bram', 2, 'Cleo', 3, 'Dara', 4, 'Emil', 5, 'Fern', 6, 'Gita', 'Hugo'),
    DECODE(MOD(n, 6), 0, 'Ashcroft', 1, 'Brandt', 2, 'Calder', 3, 'Duquesne', 4, 'Ellery', 'Fairhurst'),
    'user' || TO_CHAR(n) || '@panoply.local',
    DECODE(MOD(n, 4), 0, 'Engineering', 1, 'Finance', 2, 'Operations', 'Research'),
    CASE WHEN n > 10 THEN MOD(n, 10) + 1 ELSE NULL END,
    n * 1000000000,
    0.25 + (MOD(n, 4) * 0.25),
    CASE WHEN MOD(n, 7) = 0 THEN 0 ELSE 1 END,
    TIMESTAMP '$baseDate 00:00:00' + NUMTODSINTERVAL(n, 'DAY'),
    TIMESTAMP '$baseDate 00:00:00' + NUMTODSINTERVAL(n, 'MINUTE'),
    FROM_TZ(TIMESTAMP '$baseDate 00:00:00' + NUMTODSINTERVAL(n, 'MINUTE'), '-05:00'),
    TIMESTAMP '$baseDate 00:00:00' + NUMTODSINTERVAL(n, 'MINUTE'),
    -- Oracle stores a GUID in RAW(16) big-endian (RFC 4122), so the hex here is the same identifier
    -- SQL Server's uniqueidentifier column holds for the same row.
    HEXTORAW(LPAD(TO_CHAR(n), 8, '0') || '0000' || '4000' || '8000' || '000000000000'),
    UTL_RAW.CAST_FROM_NUMBER(n)
FROM (SELECT LEVEL AS n FROM DUAL CONNECT BY LEVEL <= $Rows)
/

INSERT INTO EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER, LAST_MODIFIED)
SELECT EMPLOYEE_ID, '+44 20 7000 ' || LPAD(TO_CHAR(EMPLOYEE_ID), 4, '0'), LAST_MODIFIED FROM EMPLOYEES
/

INSERT INTO EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER, LAST_MODIFIED)
SELECT EMPLOYEE_ID, '+44 161 496 ' || LPAD(TO_CHAR(EMPLOYEE_ID), 4, '0'), LAST_MODIFIED
FROM EMPLOYEES WHERE MOD(EMPLOYEE_ID, 3) = 0
/

-- The change logs are not seeded by hand: the triggers write the creation entries as the rows go in;
-- see the SQL Server script.
"@
}

function New-OracleSeedScript {
    param([int]$Rows)

    # WHENEVER SQLERROR EXIT FAILURE is what makes a broken seed fail the script rather than scroll past.
    # The drops are wrapped because Oracle has no DROP ... IF EXISTS and a missing object is not an error
    # worth stopping for on a first run.
    return @"
WHENEVER SQLERROR EXIT FAILURE
SET DEFINE OFF
SET SERVEROUTPUT ON
SET FEEDBACK OFF

-- The application schema JIM connects as. Created by SYSTEM, which is who this script runs as.
DECLARE
    user_exists NUMBER;
BEGIN
    SELECT COUNT(*) INTO user_exists FROM ALL_USERS WHERE USERNAME = 'JIMTEST';
    IF user_exists = 0 THEN
        EXECUTE IMMEDIATE 'CREATE USER JIMTEST IDENTIFIED BY "$($config.Password)"';
    END IF;
    -- Least privilege is the documented deployment guidance, but a test schema has to be able to build
    -- itself; UNLIMITED TABLESPACE is what lets the 500,000-row seed fit.
    EXECUTE IMMEDIATE 'GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE TRIGGER, UNLIMITED TABLESPACE TO JIMTEST';
END;
/

-- Everything from here builds inside JIMTEST's own schema.
ALTER SESSION SET CURRENT_SCHEMA = JIMTEST;

BEGIN
    FOR t IN (SELECT table_name FROM ALL_TABLES WHERE OWNER = 'JIMTEST') LOOP
        EXECUTE IMMEDIATE 'DROP TABLE JIMTEST."' || t.table_name || '" CASCADE CONSTRAINTS PURGE';
    END LOOP;
    FOR v IN (SELECT view_name FROM ALL_VIEWS WHERE OWNER = 'JIMTEST') LOOP
        EXECUTE IMMEDIATE 'DROP VIEW JIMTEST."' || v.view_name || '"';
    END LOOP;
    FOR s IN (SELECT sequence_name FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = 'JIMTEST') LOOP
        EXECUTE IMMEDIATE 'DROP SEQUENCE JIMTEST."' || s.sequence_name || '"';
    END LOOP;
END;
/

CREATE TABLE EMPLOYEES (
    EMPLOYEE_ID          $($config.Types.Integer)      NOT NULL PRIMARY KEY,
    EMPLOYEE_NUMBER      $($config.Types.NaturalAnchor) NOT NULL,
    FIRST_NAME           $($config.Types.Text)         NOT NULL,
    LAST_NAME            $($config.Types.Text)         NOT NULL,
    EMAIL                $($config.Types.Text)         NOT NULL,
    DEPARTMENT           $($config.Types.Text)         NOT NULL,
    MANAGER_EMPLOYEE_ID  $($config.Types.Integer),
    HEADCOUNT            $($config.Types.BigInteger)   NOT NULL,
    FTE                  $($config.Types.Decimal)      NOT NULL,
    IS_ACTIVE            $($config.Types.Boolean)      NOT NULL,
    START_DATE           $($config.Types.ZonelessDate) NOT NULL,
    -- The Watermark Column mode's column, defaulted on insert and moved by a trigger on update, as
    -- docs/connectors/jim-sql-connector-delta-import-watermark.md prescribes.
    LAST_MODIFIED        $($config.Types.ZonelessDate) DEFAULT SYSTIMESTAMP NOT NULL,
    HIRED_AT             $($config.Types.OffsetDate)   NOT NULL,
    -- Oracle's third date and time shape, which has no SQL Server equivalent. The catalogue reports it
    -- as offset-carrying; what ODP.NET returns for it is what the matrix is here to establish.
    HIRED_AT_LOCAL       $($config.LocalZoneDateColumn) NOT NULL,
    EMPLOYEE_GUID        $($config.Types.Guid)         NOT NULL,
    PHOTO                $($config.Types.Binary)
)
/

CREATE VIEW V_EMPLOYEES AS
    SELECT EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT,
           MANAGER_EMPLOYEE_ID, FTE, IS_ACTIVE, START_DATE, LAST_MODIFIED, HIRED_AT, EMPLOYEE_GUID
    FROM EMPLOYEES
/

CREATE TABLE EMPLOYEE_PHONES (
    PHONE_ID       $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    EMPLOYEE_ID    $($config.Types.Integer)      NOT NULL,
    PHONE_NUMBER   $($config.Types.Text)         NOT NULL,
    LAST_MODIFIED  $($config.Types.ZonelessDate) DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT FK_PHONES_EMPLOYEE FOREIGN KEY (EMPLOYEE_ID) REFERENCES EMPLOYEES (EMPLOYEE_ID)
)
/

-- The change-log Delta Import mode's source, in the shape the guide prescribes; see the SQL Server
-- script for the commentary.
CREATE TABLE IDM_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    EMPLOYEE_ID  $($config.Types.Integer)      NOT NULL,
    CHANGE_TYPE  CHAR(1)                       NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) DEFAULT SYSTIMESTAMP NOT NULL
)
/

-- The view's own change log; see the SQL Server script for why PersonView is anchored on EMAIL.
CREATE TABLE V_EMPLOYEES_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    EMAIL        $($config.Types.Text)         NOT NULL,
    CHANGE_TYPE  CHAR(1)                       NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) DEFAULT SYSTIMESTAMP NOT NULL
)
/

-- The triggers the two Delta Import guides prescribe; see the SQL Server script for the commentary.
CREATE OR REPLACE TRIGGER TRG_EMPLOYEES_IDM_CHANGE_LOG
AFTER INSERT OR UPDATE OR DELETE ON EMPLOYEES
FOR EACH ROW
BEGIN
    IF INSERTING THEN
        INSERT INTO IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE) VALUES (:NEW.EMPLOYEE_ID, 'I');
        INSERT INTO V_EMPLOYEES_CHANGE_LOG (EMAIL, CHANGE_TYPE) VALUES (:NEW.EMAIL, 'I');
    ELSIF UPDATING THEN
        INSERT INTO IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE) VALUES (:NEW.EMPLOYEE_ID, 'U');
        INSERT INTO V_EMPLOYEES_CHANGE_LOG (EMAIL, CHANGE_TYPE) VALUES (:NEW.EMAIL, 'U');
    ELSE
        INSERT INTO IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE) VALUES (:OLD.EMPLOYEE_ID, 'D');
        INSERT INTO V_EMPLOYEES_CHANGE_LOG (EMAIL, CHANGE_TYPE) VALUES (:OLD.EMAIL, 'D');
    END IF;
END;
/

-- A related table's change is a change to its parent.
CREATE OR REPLACE TRIGGER TRG_EMPLOYEE_PHONES_IDM_CHANGE_LOG
AFTER INSERT OR UPDATE OR DELETE ON EMPLOYEE_PHONES
FOR EACH ROW
BEGIN
    INSERT INTO IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE)
    VALUES (COALESCE(:NEW.EMPLOYEE_ID, :OLD.EMPLOYEE_ID), 'U');
END;
/

CREATE OR REPLACE TRIGGER TRG_EMPLOYEES_LAST_MODIFIED
BEFORE UPDATE ON EMPLOYEES
FOR EACH ROW
BEGIN
    :NEW.LAST_MODIFIED := SYSTIMESTAMP;
END;
/

CREATE OR REPLACE TRIGGER TRG_EMPLOYEE_PHONES_LAST_MODIFIED
BEFORE UPDATE ON EMPLOYEE_PHONES
FOR EACH ROW
BEGIN
    :NEW.LAST_MODIFIED := SYSTIMESTAMP;
END;
/

-- Export target with a sequence-generated key, returned via RETURNING ... INTO an output parameter.
-- The identity starts at 1,000,000 so generated keys never overlap the seeded EMPLOYEE_ID range; see
-- the SQL Server script and the anchor comment in Setup-Scenario16.ps1.
CREATE TABLE APP_USERS (
    ID            $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY (START WITH 1000000) PRIMARY KEY,
    USER_NAME     $($config.Types.Text)         NOT NULL,
    DISPLAY_NAME  $($config.Types.Text),
    EMAIL         $($config.Types.Text),
    MANAGER_ID    $($config.Types.Integer),
    FTE           $($config.Types.Decimal),
    IS_ENABLED    $($config.Types.Boolean)      NOT NULL,
    STARTS_ON     $($config.Types.ZonelessDate),
    STARTS_AT     $($config.Types.OffsetDate),
    -- A watermark column on every table in the shared document; see the SQL Server script.
    LAST_MODIFIED $($config.Types.ZonelessDate) DEFAULT SYSTIMESTAMP NOT NULL
)
/

CREATE TABLE APP_USER_ROLES (
    ROLE_ID        $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    USER_ID        $($config.Types.Integer)      NOT NULL,
    ROLE_NAME      $($config.Types.Text)         NOT NULL,
    LAST_MODIFIED  $($config.Types.ZonelessDate) DEFAULT SYSTIMESTAMP NOT NULL,
    CONSTRAINT FK_ROLES_USER FOREIGN KEY (USER_ID) REFERENCES APP_USERS (ID) ON DELETE CASCADE
)
/

CREATE TABLE APP_ACCOUNTS_NATURAL (
    ACCOUNT_CODE  $($config.Types.NaturalAnchor) NOT NULL PRIMARY KEY,
    DISPLAY_NAME  $($config.Types.Text),
    IS_ENABLED    $($config.Types.Boolean)       NOT NULL,
    LAST_MODIFIED $($config.Types.ZonelessDate)  DEFAULT SYSTIMESTAMP NOT NULL
)
/

-- The RAW(16) anchor table. This exists for Oracle alone and is the reason the matrix has an Oracle
-- specific row: a key that is 16 opaque bytes defaulted from SYS_GUID() is what proves both that the
-- driver hands a RAW(16) back as bytes on import, and that a generated one comes back from
-- RETURNING ... INTO on export.
CREATE TABLE GUID_KEYED_PEOPLE (
    PERSON_ID      $($config.Types.Guid) DEFAULT SYS_GUID() NOT NULL PRIMARY KEY,
    FULL_NAME      $($config.Types.Text) NOT NULL,
    DEPARTMENT     $($config.Types.Text),
    LAST_MODIFIED  $($config.Types.ZonelessDate) DEFAULT SYSTIMESTAMP NOT NULL
)
/

-- A change log per export-target Object Type; see the SQL Server script for why every selected Object
-- Type needs one and why these stay empty.
CREATE TABLE APP_USERS_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    ID           $($config.Types.Integer)      NOT NULL,
    CHANGE_TYPE  CHAR(1)                       NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) NOT NULL
)
/

CREATE TABLE APP_ACCOUNTS_CHANGE_LOG (
    CHANGE_ID     $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    ACCOUNT_CODE  $($config.Types.NaturalAnchor) NOT NULL,
    CHANGE_TYPE   CHAR(1)                        NOT NULL,
    CHANGED_AT    $($config.Types.ZonelessDate)  NOT NULL
)
/

CREATE TABLE GUID_PEOPLE_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    PERSON_ID    $($config.Types.Guid)         NOT NULL,
    CHANGE_TYPE  CHAR(1)                       NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) NOT NULL
)
/

CREATE TABLE JIM_SEED_MANIFEST (
    SEED_HASH  VARCHAR2(64) NOT NULL PRIMARY KEY,
    ROW_COUNT  NUMBER(10)   NOT NULL,
    SEEDED_AT  TIMESTAMP(3) NOT NULL
)
/

$(New-OracleSourceRowInserts -Rows $Rows)

-- Three rows whose keys the database authors, so an import of a RAW(16) anchor has something to read
-- before any export has run.
INSERT INTO GUID_KEYED_PEOPLE (FULL_NAME, DEPARTMENT)
SELECT 'Guid Person ' || TO_CHAR(n), 'Research' FROM (SELECT LEVEL AS n FROM DUAL CONNECT BY LEVEL <= 3)
/

INSERT INTO JIM_SEED_MANIFEST (SEED_HASH, ROW_COUNT, SEEDED_AT)
VALUES ('{0}', $Rows, SYS_EXTRACT_UTC(SYSTIMESTAMP))
/

COMMIT
/

SET FEEDBACK ON
SELECT 'SEEDED' AS RESULT, COUNT(*) AS EMPLOYEES FROM EMPLOYEES;
EXIT SUCCESS
"@
}

# The export-target tables are written by JIM, not by the seeder, so the seed hash says nothing about
# their state: a preserved database volume carries one run's exported rows into the next, where a fresh
# JIM stages Creates for people who already have rows. Natural-key inserts then die on the primary key,
# database-generated keys duplicate silently, and every export row of the matrix fails against residue
# rather than against the code under test. These scripts run on the hash-match path, so the expensive
# source seed is still skipped while JIM's own targets always start empty. Identity counters are
# deliberately NOT restarted: continuing from a higher value never overlaps the seeded EMPLOYEE_ID
# range, which is all the seed's START WITH 1,000,000 exists to guarantee, and the matrix reads
# generated keys back from the database rather than asserting absolute values.

function New-SqlServerExportTargetResetScript {
    param([int]$Rows)

    return @"
SET NOCOUNT ON;
GO

USE JIMTEST;
GO

DELETE FROM hr.APP_USER_ROLES;
DELETE FROM hr.APP_USERS;
DELETE FROM hr.APP_ACCOUNTS_NATURAL;
DELETE FROM hr.APP_USERS_CHANGE_LOG;
DELETE FROM hr.APP_ACCOUNTS_CHANGE_LOG;
GO

-- The matrix's rows mutate the SOURCE tables too: Export.Update rewrites an email and adds a phone
-- number, Export.Delete disables employee 20. Those edits used to survive the hash-skip, so employee
-- 20 arrived at the next run's baseline already disabled, was never in the outbound rule's scope, and
-- the export rows failed against residue rather than against the code under test. The source rows are
-- restored wholesale from the same SQL the seed uses, so any cell mutation, present or future, is
-- undone without this script having to know about it.
-- The change logs are cleared AFTER the source rows go (the triggers log the deletions as they go)
-- and BEFORE they come back (the triggers log the creations), so the log ends exactly as a fresh
-- seed leaves it.
DELETE FROM hr.EMPLOYEE_PHONES;
DELETE FROM hr.EMPLOYEES;
DELETE FROM hr.IDM_CHANGE_LOG;
DELETE FROM hr.V_EMPLOYEES_CHANGE_LOG;
GO

$(New-SqlServerSourceRowInserts -Rows $Rows)

SELECT 'RESET' AS RESULT;
GO
"@
}

function New-OracleExportTargetResetScript {
    param([int]$Rows)

    return @"
WHENEVER SQLERROR EXIT FAILURE
SET DEFINE OFF
SET FEEDBACK OFF

ALTER SESSION SET CURRENT_SCHEMA = JIMTEST;

DELETE FROM APP_USER_ROLES;
DELETE FROM APP_USERS;
DELETE FROM APP_ACCOUNTS_NATURAL;
DELETE FROM GUID_KEYED_PEOPLE;
DELETE FROM APP_USERS_CHANGE_LOG;
DELETE FROM APP_ACCOUNTS_CHANGE_LOG;
DELETE FROM GUID_PEOPLE_CHANGE_LOG;

-- The matrix's rows mutate the SOURCE tables too (see the SQL Server reset above for the history);
-- restore them wholesale from the same SQL the seed uses.
-- Change logs cleared after the source rows go and before they come back; see the SQL Server reset.
DELETE FROM EMPLOYEE_PHONES;
DELETE FROM EMPLOYEES;
DELETE FROM IDM_CHANGE_LOG;
DELETE FROM V_EMPLOYEES_CHANGE_LOG;

$(New-OracleSourceRowInserts -Rows $Rows)

-- The three rows whose keys the database authors, restored exactly as the main seed writes them, so an
-- import of a RAW(16) anchor has something to read before any export has run.
INSERT INTO GUID_KEYED_PEOPLE (FULL_NAME, DEPARTMENT)
SELECT 'Guid Person ' || TO_CHAR(n), 'Research' FROM (SELECT LEVEL AS n FROM DUAL CONNECT BY LEVEL <= 3)
/

COMMIT
/

SET FEEDBACK ON
SELECT 'RESET' AS RESULT FROM DUAL;
EXIT SUCCESS
"@
}

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# Execution
# ─────────────────────────────────────────────────────────────────────────────────────────────────

function Invoke-ContainerSqlScript {
    <#
    .SYNOPSIS
        Copy a SQL script into the database container and run it there.
    .DESCRIPTION
        Running inside the container rather than from the test host is what keeps the seeder free of any
        client tooling requirement: sqlcmd and SQL*Plus already ship in the respective images, and no
        port has to be published to reach them.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][string]$ScriptText,
        [Parameter(Mandatory=$true)][string]$ScriptName
    )

    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) $ScriptName

    # UTF-8 without a byte order mark: SQL*Plus reads a BOM as part of the first statement.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($localPath, $ScriptText, $utf8NoBom)

    try {
        docker cp $localPath "$($Config.ContainerName):/tmp/$ScriptName" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not copy $ScriptName into $($Config.ContainerName)."
        }

        $arguments = @("exec", $Config.ContainerName) + $Config.SqlCommand

        # SQL*Plus takes the script as '@/path' with no space; sqlcmd takes '-i' then the path.
        if ($Config.ScriptArgument -eq "@") {
            $arguments += "@/tmp/$ScriptName"
        }
        else {
            $arguments += @($Config.ScriptArgument, "/tmp/$ScriptName")
        }

        $output = & docker @arguments 2>&1
        $exitCode = $LASTEXITCODE

        # SQL*Plus exits 0 for a script that failed unless WHENEVER SQLERROR is set (it is, above), and
        # Oracle reports failures as ORA- text either way, so the output is checked as well as the code.
        $outputText = ($output | Out-String)
        $hasOracleError = $outputText -match 'ORA-\d{5}'
        $hasSqlServerError = $outputText -match 'Msg \d+, Level 1[6-9]'

        if ($exitCode -ne 0 -or $hasOracleError -or $hasSqlServerError) {
            throw "Seeding $($Config.DisplayName) failed (exit $exitCode):`n$outputText"
        }

        return $outputText
    }
    finally {
        Remove-Item $localPath -ErrorAction SilentlyContinue
    }
}

function Get-SeededHash {
    <#
    .SYNOPSIS
        Read the content hash of whatever is currently seeded, or $null if nothing is.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Config)

    if ($Config.Provider -eq "SqlServer") {
        $query = "SET NOCOUNT ON; IF DB_ID('JIMTEST') IS NOT NULL AND OBJECT_ID('JIMTEST.hr.JIM_SEED_MANIFEST') IS NOT NULL SELECT TOP 1 SEED_HASH FROM JIMTEST.hr.JIM_SEED_MANIFEST;"
        $arguments = @("exec", $Config.ContainerName) + $Config.SqlCommand + @("-h", "-1", "-W", "-Q", $query)
    }
    else {
        $query = "SET HEADING OFF`nSET FEEDBACK OFF`nSET PAGESIZE 0`nSELECT SEED_HASH FROM JIMTEST.JIM_SEED_MANIFEST WHERE ROWNUM = 1;`nEXIT"
        return Get-OracleSeededHash -Config $Config -Query $query
    }

    $output = & docker @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }

    $hash = ($output | Out-String).Trim()
    if ($hash -match '^[0-9A-Fa-f]{64}$') { return $hash }
    return $null
}

function Get-OracleSeededHash {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][string]$Query
    )

    # SQL*Plus has no -Q equivalent, so the probe goes in as a script like everything else.
    $probeName = "jim-seed-probe.sql"
    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) $probeName
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($localPath, $Query, $utf8NoBom)

    try {
        docker cp $localPath "$($Config.ContainerName):/tmp/$probeName" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { return $null }

        $arguments = @("exec", $Config.ContainerName) + $Config.SqlCommand + @("@/tmp/$probeName")
        $output = & docker @arguments 2>&1
        if ($LASTEXITCODE -ne 0) { return $null }

        foreach ($line in ($output | Out-String) -split "`n") {
            $candidate = $line.Trim()
            if ($candidate -match '^[0-9A-Fa-f]{64}$') { return $candidate }
        }
        return $null
    }
    finally {
        Remove-Item $localPath -ErrorAction SilentlyContinue
    }
}

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# Main
# ─────────────────────────────────────────────────────────────────────────────────────────────────

Write-TestSection "Scenario 16 Seed: $($config.DisplayName)"
Write-Host "  Container:  $($config.ContainerName)" -ForegroundColor Gray
Write-Host "  Row count:  $RowCount" -ForegroundColor Gray

$scriptTemplate = if ($Provider -eq "SqlServer") { New-SqlServerSeedScript -Rows $RowCount } else { New-OracleSeedScript -Rows $RowCount }

# The hash covers the generated script itself, so it changes whenever the schema, the generated values
# or the row count change, and only then. That is the Generate-TestCSV.ps1 contract: identical inputs
# skip the work, and any edit to this file invalidates every cached database automatically.
$hashBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($scriptTemplate))
$seedHash = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()

Write-Host "  Seed hash:  $seedHash" -ForegroundColor Gray

if (-not $Force) {
    $existingHash = Get-SeededHash -Config $config
    if ($existingHash -eq $seedHash) {
        # The hash covers the source data alone. The export targets hold whatever JIM wrote last run,
        # so they are reset here even when the seed itself is skipped; see the reset scripts above.
        Write-Host "  OK Source data already matches this seed; resetting export targets only (pass -Force to re-seed)" -ForegroundColor Green
        $resetScript = if ($Provider -eq "SqlServer") { New-SqlServerExportTargetResetScript -Rows $RowCount } else { New-OracleExportTargetResetScript -Rows $RowCount }
        $resetResult = Invoke-ContainerSqlScript -Config $config -ScriptText $resetScript -ScriptName "jim-scenario16-reset-$($Provider.ToLowerInvariant()).sql"
        if ($resetResult -notmatch 'RESET') {
            throw "Resetting $($config.DisplayName)'s export targets produced no completion marker. Output:`n$resetResult"
        }
        return @{ Provider = $Provider; RowCount = $RowCount; SeedHash = $seedHash; Reseeded = $false }
    }
    if ($existingHash) {
        Write-Host "  Existing seed $existingHash does not match; re-seeding" -ForegroundColor Yellow
    }
}

# The placeholder is filled only now, so the hash describes the script's meaning rather than including
# itself, which it could not.
$scriptText = $scriptTemplate -replace '\{0\}', $seedHash

$seedStart = Get-Date
$result = Invoke-ContainerSqlScript -Config $config -ScriptText $scriptText -ScriptName "jim-scenario16-seed-$($Provider.ToLowerInvariant()).sql"
$elapsed = (Get-Date) - $seedStart

if ($result -notmatch 'SEEDED') {
    throw "Seeding $($config.DisplayName) produced no completion marker. Output:`n$result"
}

Write-Host "  OK Seeded $RowCount employee row(s) in $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green

return @{ Provider = $Provider; RowCount = $RowCount; SeedHash = $seedHash; Reseeded = $true }
