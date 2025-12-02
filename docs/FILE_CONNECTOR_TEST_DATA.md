# File Connector Test Data Setup

## Overview

The JIM development environment automatically creates a symlink from `connector-files/test-data/` to `test/Data/` during devcontainer setup. This allows the File Connector to access test data files without duplicating them in the repository.

## How It Works

1. **Automatic Setup**: When the devcontainer is created or rebuilt, the [setup.sh](.devcontainer/setup.sh) script automatically:
   - Creates the `connector-files/` directory if it doesn't exist
   - Creates a symlink: `connector-files/test-data` → `test/Data`

2. **Dynamic Access**: The symlink provides real-time access to files:
   - Any CSV files added to `test/Data/` automatically appear in `connector-files/test-data/`
   - No duplication - files only exist once in the repository
   - Changes to files are immediately visible through the symlink

3. **No Manual Steps**: The symlink is created automatically during devcontainer initialization - no manual intervention required.

## Available Test Data Files

The following test data files are available in `test/Data/` and accessible via the symlink:

- `Users.csv` - Sample user data
- `Firstnames-f.csv` - Female first names
- `Firstnames-m.csv` - Male first names
- `Lastnames.csv` - Last names (default)
- `Lastnames-fr.csv` - French last names
- `Lastnames-it.csv` - Italian last names
- `Adjectives.en.csv` - English adjectives
- `Colours.en.csv` - English colour names
- `GroupNameEndings.en.csv` - Group name endings

## Using Test Data with File Connector

When creating a File Connector Run Profile in the JIM UI:

1. Navigate to **Admin** → **Connected Systems** → Select your File Connector
2. Go to the **Run Profiles** tab
3. Create a new Run Profile
4. In the **File Path** field, use the in-container path:
   ```
   /var/connector-files/test-data/Users.csv
   ```
   Or any other CSV file from the test-data folder.

## Adding New Test Data

To add new test data files:

1. Add your CSV file to the `test/Data/` directory in the repository
2. The file will automatically be available at `/var/connector-files/test-data/YourFile.csv` (no restart needed)
3. Commit the file to the repository so it's available to all developers

## Technical Details

- **Symlink Type**: Standard Linux symbolic link
- **Target**: `test/Data/` (relative to workspace root)
- **Link Location**: `connector-files/test-data`
- **Container Path**: `/var/connector-files/test-data/` (when running in Docker)
- **Permissions**: Inherits permissions from the target directory

## Benefits

✅ **No Duplication**: Test data exists only once in the repository  
✅ **Version Control**: Test data files are tracked in Git  
✅ **Automatic Updates**: New files appear immediately without manual copying  
✅ **Shared Access**: All developers get the same test data automatically  
✅ **Easy Maintenance**: Update test data in one place (`test/Data/`)

## Troubleshooting

**Symlink not created?**
- Rebuild the devcontainer: `Ctrl+Shift+P` → "Dev Containers: Rebuild Container"
- Manually run: `bash .devcontainer/setup.sh`

**Files not visible through symlink?**
- Check the source directory: `ls test/Data/`
- Verify symlink exists: `ls -lah connector-files/`
- Confirm symlink target: `readlink connector-files/test-data`

**Permission issues?**
- Symlinks inherit permissions from the target directory
- Check `test/Data/` permissions: `ls -lah test/Data/`
