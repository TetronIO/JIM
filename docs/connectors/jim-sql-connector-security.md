---
title: SQL Connector Security
description: The database permissions the SQL Connector needs, encrypting its connection, how it keeps its password out of logs, and what you trust it with.
type: object
audience: administrator
---

# SQL Connector Security

What the [SQL Connector](jim-sql-connector.md)'s database account needs, how its connection is encrypted, and what an administrator who configures it is trusted with.

```jim-diagram
archetype: comparison
title: How each database's connection is protected
desc: "A Microsoft SQL Server connection is encrypted with TLS by default, and fails rather than fall back to plain text. Its certificate is always validated, and a certificate from an authority JIM does not yet trust can be trusted from the card the failed connection test shows. It runs unencrypted only if Encrypt Connection is turned off. An Oracle Database connection uses Native Network Encryption by default (AES, with SHA-2 integrity checking), which needs no certificate. Over TCPS (TLS) instead, the server's certificate must be one the operating system already trusts, because the Oracle driver accepts no additional trust anchors. Its None setting sends data unencrypted, for a lab only. For both, the password is stored encrypted and never logged, and the account needs no db_owner, DBA or SELECT ANY TABLE."
columns:
  - { label: Microsoft SQL Server }
  - { label: Oracle Database }
rows:
  - label: By default
    cells:
      - { text: TLS, sub: never falls back to plain text }
      - { text: Native Network Encryption, sub: "AES, with SHA-2 integrity checking" }
  - label: Certificate
    cells:
      - { text: always validated, sub: "an unknown authority: trust it from the failed test's card" }
      - { text: "none needed, except over TCPS", sub: "TCPS: one the operating system already trusts" }
  - label: Unencrypted
    cells:
      - { text: only with Encrypt Connection off }
      - { text: "`None`, for a lab only" }
footer: "Both: the password is stored encrypted and never logged, and no `db_owner`, `DBA` or `SELECT ANY TABLE` is needed"
```

## :material-account-lock-outline: Database account permissions

Create a dedicated database account for JIM and grant it only what the Run Profiles you configure will use:

- **For schema discovery and import**<br /> `SELECT` on each table or view in the Object Types document, on each related table, and (for Change-Log Table Delta Imports) on each change-log table. JIM reads the database's own catalogue views, which show the account exactly the objects it can already read; no wider catalogue permission is needed.
- **For export**<br /> `INSERT`, `UPDATE` and `DELETE` on each Object Type's table and each related table, in addition to `SELECT`.
- **For probing generated values**<br /> `SELECT` on each table or view a generated value is exported to, which import already needs; on Microsoft SQL Server the same grant lets JIM read the column's collation from the catalogue. See [Probing for values already in use](jim-sql-connector-values-in-use.md).
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

## :material-lock-outline: Encryption in transit

- **SQL Server** connections are encrypted by default and fail rather than fall back to plain text. The server's certificate is always validated; JIM never accepts an unvalidated certificate.
- **Oracle** connections use Native Network Encryption by default (AES, with SHA-2 integrity checking), or TLS over a TCPS listener if you choose it. Choose **None** only in a lab.
- Nothing needs installing on the JIM host for either database, and JIM connects only to the host you name, so the connector works unchanged in an air-gapped deployment.

### What an Oracle server needs for Native Network Encryption

Nothing, if its network settings are at Oracle's defaults: the server accepts encryption without requiring it, and JIM requires it, so the session is encrypted. JIM asks for AES256, AES192 or AES128 encryption with SHA512, SHA384 or SHA256 integrity checking, and refuses to connect rather than fall back to plain text.

The connection test fails if the server's `sqlnet.ora`:

- sets `SQLNET.ENCRYPTION_SERVER` or `SQLNET.CRYPTO_CHECKSUM_SERVER` to `REJECTED`;
- or limits `SQLNET.ENCRYPTION_TYPES_SERVER` or `SQLNET.CRYPTO_CHECKSUM_TYPES_SERVER` to algorithms outside those lists, such as `SHA1`.

To make the server refuse any client that does not encrypt, set `SQLNET.ENCRYPTION_SERVER` and `SQLNET.CRYPTO_CHECKSUM_SERVER` to `REQUIRED`. JIM connects either way.

### When the connection test fails on the certificate

For SQL Server, if the server presents a certificate JIM does not yet trust (one issued by an internal authority, or self-signed), the connection test fails and shows you the certificate: who issued it, what name it was issued for, its validity dates and its thumbprint, which check it failed, and the certificate chain JIM could find above it. Choose what to trust from the card: the root (recommended, since it survives the renewal of every certificate beneath it), an intermediate authority, or the server's own certificate, which needs trusting again when it is renewed. Any of them makes the connection work.

SQL Server sends only its own certificate, not the authorities above it. JIM fetches them from the download address the certificate names, where it names one, and stores any it had to fetch alongside the certificate you trust, so the connection does not depend on that address afterwards. Where the certificate names no address, the card offers the server's own certificate alone; to trust its authority instead, get the authority's certificate from whoever runs your PKI, add it under **Admin > Certificates**, and test again.

An expired certificate, or one issued for a different name from the host you connect to, is still refused; renew it, or connect by a name the certificate carries. So is a chain through a certificate authority that has expired or is not marked as one; that has to be fixed on the server.

For Oracle over TCPS, JIM shows the same detail, but the certificate must be one your operating system already trusts; the Oracle driver does not accept additional trust anchors, so JIM does not offer to trust the certificate, and judges it against the operating system alone. Native Network Encryption needs no certificate at all, which is one reason it is the default.

## :material-shield-lock-outline: Credentials and logging

The password is stored encrypted and is used only to open the connection. It does not appear in any log, Activity or configuration history, and the database driver's error messages never include it.

## :material-scale-balance: What you are trusting

The Object Types document, including any `select` statement, is administrator configuration: it decides which tables JIM reads and writes, just as a Synchronisation Rule decides what flows where. Everything JIM sends to the database as data is sent as a parameter, never pasted into a statement, and every table and column name is validated and quoted.

## :material-book-open-variant-outline: Related

- [JIM SQL Connector](jim-sql-connector.md): the Transport Security and Credentials settings.
- [Troubleshooting the SQL Connector](jim-sql-connector-troubleshooting.md)
