# JIM File Connector

## Overview

The JIM File Connector enables bi-directional synchronisation of identity data with CSV and delimited text files. It is ideal for integrating with systems that produce flat-file extracts, such as HR exports, batch feeds, or data migration files.

**Capabilities:** Full Import, Export

## Supported Features

- **Configurable delimiters** -- comma (default), tab, pipe, semicolon, or any custom character
- **Multi-valued attributes** -- supported via duplicate column names in the header row, or via a configurable in-field delimiter (default: pipe `|`)
- **Schema auto-discovery** -- column headers are read automatically to build the schema
- **Type detection** -- attribute data types are inferred by inspecting up to 50 rows of data. Supported types: Text, Number, Boolean, Guid, DateTime
- **Culture-aware parsing** -- an optional culture setting controls how numbers, dates, and other locale-sensitive values are parsed
- **Object type support** -- objects can be typed via a dedicated column in the file, or by specifying a fixed object type for the entire file
- **Operational modes** -- Import Only, Export Only, or Bidirectional (export then confirming import from the same file)
- **Auto-confirm export** -- in Export Only mode, exported changes are written directly and automatically confirmed
- **Stop on first error** -- optionally halt processing on the first error encountered, useful for debugging data quality issues

## Settings

| Setting | Description | Default | Example |
|---------|-------------|---------|---------|
| File Path | Path to the CSV file inside the container. Used for import, export, or both depending on mode. | *(required)* | `/var/connector-files/Users.csv` |
| Mode | Operational mode: Import Only, Export Only, or Bidirectional. | `Import Only` | `Bidirectional` |
| Object Type Column | Column in the file that contains the object type. Use when the file contains multiple object types. | *(optional)* | `Type` |
| Object Type | Fixed object type name. Use when the file contains a single type of object. | *(optional)* | `User` |
| Delimiter | Field delimiter character. | `,` | `\t` (tab) |
| Multi-Value Delimiter | Character used to separate multiple values within a single field. | `\|` | `;` |
| Culture | Culture code for parsing locale-sensitive values. Uses invariant culture if not specified. | *(invariant)* | `en-gb` |
| Stop On First Error | Stop processing when the first error is encountered. Useful for debugging. | `false` | `true` |

!!! note "Object type configuration"
    You must specify either **Object Type Column** or **Object Type** (but not both). If the file contains multiple object types in different rows, use Object Type Column to point to the column that identifies each row's type. If every row represents the same type, use Object Type to set a fixed value.

## Docker Volume Setup

The File Connector reads from and writes to the local filesystem inside the JIM container. To make files accessible, mount a host directory as a Docker volume mapped to `/var/connector-files/` (or any path of your choosing).

=== "Docker Compose"

    ```yaml
    services:
      jim-worker:
        volumes:
          - ./connector-files:/var/connector-files
    ```

=== "Docker Run"

    ```bash
    docker run -v /path/to/files:/var/connector-files jim-worker
    ```

Once the volume is mounted, configure the **File Path** setting in JIM to reference the file inside the container, e.g. `/var/connector-files/Users.csv`.

!!! warning "File permissions"
    Ensure the JIM container process has read access to import files and write access to the directory for export files. On Linux hosts, check that the file ownership and permissions allow the container's user to access the mounted volume.

## Schema Discovery

When you configure a File Connector connected system and trigger schema discovery, JIM performs the following steps:

1. **Reads the header row** -- column names become attribute names in the schema.
2. **Detects multi-valued attributes** -- if the same column name appears more than once in the header, the attribute is marked as multi-valued.
3. **Infers data types** -- JIM reads up to 50 data rows and attempts to parse each column's values as Number, Boolean, Guid, or DateTime. If none of these match, the attribute defaults to Text.
4. **Discovers object types** -- if an Object Type Column is configured, JIM reads through the file to find all unique object type values. Otherwise, it uses the fixed Object Type setting.

In **Export Only** mode where no file exists yet, schema discovery creates a minimal schema with just the specified object type. Attributes are defined later by sync rules.

## Troubleshooting

### File not found

The error `File not found: '/var/connector-files/Users.csv'` indicates that the file is not accessible at the specified path inside the container.

- Verify the Docker volume mount is correctly configured.
- Check that the file exists on the host at the expected location.
- Ensure the path in the File Path setting matches the container-side mount point exactly (paths are case-sensitive on Linux).

### Column count mismatch

The error `Row X has Y columns but the header defines Z columns` means a data row has fewer or more fields than the header row.

- Open the file and inspect the row number mentioned in the error.
- Check for missing delimiters, extra delimiters, or unquoted fields that contain the delimiter character.
- Fields containing the delimiter character should be enclosed in double quotes.

### Object type column not found

The error `Object type column 'Type' not found in file headers` means the column name specified in Object Type Column does not match any column in the file.

- Check the spelling and case of the column name -- the match is case-insensitive, but the column must exist in the header row.
- If the file does not have an object type column, use the Object Type setting instead.

### Encoding issues

If imported data contains garbled characters or unexpected symbols:

- Set the **Culture** setting to match the file's locale (e.g. `en-gb`, `de-de`).
- Ensure the file is saved in UTF-8 encoding.
- If the file was exported from another system, check whether that system uses a different character encoding.

### Multi-valued attribute not detected

If attributes that should be multi-valued are appearing as single-valued:

- **Duplicate column approach**: Ensure the column name appears more than once in the header row. The names must match exactly (case-insensitive).
- **In-field delimiter approach**: Check that the Multi-Value Delimiter setting matches the delimiter used within the field values.
