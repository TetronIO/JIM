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
    LAST_MODIFIED        $($config.Types.ZonelessDate) NOT NULL,
    -- Offset-carrying: unambiguous at the wire level, so no setting applies to it.
    HIRED_AT             $($config.Types.OffsetDate)   NOT NULL,
    EMPLOYEE_GUID        $($config.Types.Guid)         NOT NULL,
    PHOTO                $($config.Types.Binary)       NULL
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
    LAST_MODIFIED  $($config.Types.ZonelessDate) NOT NULL,
    CONSTRAINT FK_PHONES_EMPLOYEE FOREIGN KEY (EMPLOYEE_ID) REFERENCES hr.EMPLOYEES (EMPLOYEE_ID)
);
GO

-- The change-log Delta Import mode's source: the only mode that observes a deletion.
CREATE TABLE hr.IDM_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Anchor)       NOT NULL PRIMARY KEY,
    EMPLOYEE_ID  $($config.Types.Integer)      NOT NULL,
    CHANGE_TYPE  nchar(1)                      NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) NOT NULL
);
GO

-- Export target with a database-generated key, returned as the external ID via OUTPUT INSERTED.
CREATE TABLE hr.APP_USERS (
    ID            $($config.Types.Anchor)       NOT NULL PRIMARY KEY,
    USER_NAME     $($config.Types.Text)         NOT NULL,
    DISPLAY_NAME  $($config.Types.Text)         NULL,
    EMAIL         $($config.Types.Text)         NULL,
    MANAGER_ID    $($config.Types.Integer)      NULL,
    FTE           $($config.Types.Decimal)      NULL,
    IS_ENABLED    $($config.Types.Boolean)      NOT NULL,
    STARTS_ON     $($config.Types.ZonelessDate) NULL,
    STARTS_AT     $($config.Types.OffsetDate)   NULL
);
GO

CREATE TABLE hr.APP_USER_ROLES (
    ROLE_ID    $($config.Types.Anchor)  NOT NULL PRIMARY KEY,
    USER_ID    $($config.Types.Integer) NOT NULL,
    ROLE_NAME  $($config.Types.Text)    NOT NULL,
    CONSTRAINT FK_ROLES_USER FOREIGN KEY (USER_ID) REFERENCES hr.APP_USERS (ID) ON DELETE CASCADE
);
GO

-- Export target with a natural primary key, which JIM must author itself (WritableOnCreate).
CREATE TABLE hr.APP_ACCOUNTS_NATURAL (
    ACCOUNT_CODE  $($config.Types.NaturalAnchor) NOT NULL PRIMARY KEY,
    DISPLAY_NAME  $($config.Types.Text)          NULL,
    IS_ENABLED    $($config.Types.Boolean)       NOT NULL
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
    CONCAT('E', RIGHT(CONCAT('00000000', CAST(n AS varchar(20))), 8)),
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

-- One creation entry per employee, so a Delta Import from a zero watermark sees the whole population.
INSERT INTO hr.IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE, CHANGED_AT)
SELECT EMPLOYEE_ID, 'I', LAST_MODIFIED FROM hr.EMPLOYEES;
GO

INSERT INTO hr.JIM_SEED_MANIFEST (SEED_HASH, ROW_COUNT, SEEDED_AT)
VALUES ('{0}', $Rows, SYSUTCDATETIME());
GO

SELECT 'SEEDED' AS Result, COUNT(*) AS Employees FROM hr.EMPLOYEES;
GO
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
    EXECUTE IMMEDIATE 'GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, UNLIMITED TABLESPACE TO JIMTEST';
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
    LAST_MODIFIED        $($config.Types.ZonelessDate) NOT NULL,
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
    LAST_MODIFIED  $($config.Types.ZonelessDate) NOT NULL,
    CONSTRAINT FK_PHONES_EMPLOYEE FOREIGN KEY (EMPLOYEE_ID) REFERENCES EMPLOYEES (EMPLOYEE_ID)
)
/

CREATE TABLE IDM_CHANGE_LOG (
    CHANGE_ID    $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    EMPLOYEE_ID  $($config.Types.Integer)      NOT NULL,
    CHANGE_TYPE  CHAR(1)                       NOT NULL,
    CHANGED_AT   $($config.Types.ZonelessDate) NOT NULL
)
/

-- Export target with a sequence-generated key, returned via RETURNING ... INTO an output parameter.
CREATE TABLE APP_USERS (
    ID            $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    USER_NAME     $($config.Types.Text)         NOT NULL,
    DISPLAY_NAME  $($config.Types.Text),
    EMAIL         $($config.Types.Text),
    MANAGER_ID    $($config.Types.Integer),
    FTE           $($config.Types.Decimal),
    IS_ENABLED    $($config.Types.Boolean)      NOT NULL,
    STARTS_ON     $($config.Types.ZonelessDate),
    STARTS_AT     $($config.Types.OffsetDate)
)
/

CREATE TABLE APP_USER_ROLES (
    ROLE_ID    $($config.Types.Integer) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    USER_ID    $($config.Types.Integer) NOT NULL,
    ROLE_NAME  $($config.Types.Text)    NOT NULL,
    CONSTRAINT FK_ROLES_USER FOREIGN KEY (USER_ID) REFERENCES APP_USERS (ID) ON DELETE CASCADE
)
/

CREATE TABLE APP_ACCOUNTS_NATURAL (
    ACCOUNT_CODE  $($config.Types.NaturalAnchor) NOT NULL PRIMARY KEY,
    DISPLAY_NAME  $($config.Types.Text),
    IS_ENABLED    $($config.Types.Boolean)       NOT NULL
)
/

-- The RAW(16) anchor table. This exists for Oracle alone and is the reason the matrix has an Oracle
-- specific row: a key that is 16 opaque bytes defaulted from SYS_GUID() is what proves both that the
-- driver hands a RAW(16) back as bytes on import, and that a generated one comes back from
-- RETURNING ... INTO on export.
CREATE TABLE GUID_KEYED_PEOPLE (
    PERSON_ID   $($config.Types.Guid) DEFAULT SYS_GUID() NOT NULL PRIMARY KEY,
    FULL_NAME   $($config.Types.Text) NOT NULL,
    DEPARTMENT  $($config.Types.Text)
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

-- Set-based generation. CONNECT BY LEVEL against DUAL is Oracle's generated series.
INSERT INTO EMPLOYEES
    (EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT, MANAGER_EMPLOYEE_ID,
     HEADCOUNT, FTE, IS_ACTIVE, START_DATE, LAST_MODIFIED, HIRED_AT, HIRED_AT_LOCAL, EMPLOYEE_GUID, PHOTO)
SELECT
    n,
    'E' || LPAD(TO_CHAR(n), 8, '0'),
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

INSERT INTO IDM_CHANGE_LOG (EMPLOYEE_ID, CHANGE_TYPE, CHANGED_AT)
SELECT EMPLOYEE_ID, 'I', LAST_MODIFIED FROM EMPLOYEES
/

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
        Write-Host "  OK Database already matches this seed; skipping (pass -Force to re-seed)" -ForegroundColor Green
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
