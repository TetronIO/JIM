# JIM Test Data

This directory contains SQL scripts for seeding test data into JIM databases for development and UI testing.

## Available Scripts

### seed-change-history.sql

Generates realistic change history data for testing the Change History UI feature.

**What it creates:**
- 2 Person MVOs: Alice Anderson and Bob Brown with extensive change history
- 2 Group MVOs: Engineers and Platform Team with membership changes
- ~17 total change records covering all scenarios:
  - Promotions and salary changes
  - Department and email updates
  - Manager reference add/remove/re-add cycles
  - Group membership changes
  - Name and description updates

**How to run:**

```bash
# Option 1: Pipe from host (recommended - works in devcontainer)
docker compose exec -T jim.database psql -U jim -d jim < test/data/seed-change-history.sql

# Option 2: Copy into container then run
docker cp test/data/seed-change-history.sql jim.database:/tmp/
docker compose exec jim.database psql -U jim -d jim -f /tmp/seed-change-history.sql

# Option 3: From host machine (if PostgreSQL client installed locally)
psql -h localhost -p 5432 -U jim -d jim -f test/data/seed-change-history.sql
```

**Note:** The current script has schema mismatches with the database. You'll need to either:
1. Run an integration test to generate real change history data (recommended)
2. Update the SQL script to match the current schema (see CLAUDE.md maintenance section)

**After running:**
The script will output URLs like:
```
UI Testing URLs:
  Alice: /t/people/v/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
  Bob: /t/people/v/yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy
  Engineers: /t/groups/v/zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz
  Platform Team: /t/groups/v/wwwwwwww-wwww-wwww-wwww-wwwwwwwwwwww
```

Copy these URLs and navigate to them in your browser (http://localhost:5200) to view the change history timeline.

## Maintenance

If the database schema changes (migrations affecting change tracking tables), the SQL scripts must be regenerated. See `CLAUDE.md` section "Test Data Generation" for detailed instructions.

**Quick schema check:**
```bash
# View recent migrations
ls -lt JIM.PostgresData/Migrations/ | head -20

# Check for changes to change tracking tables
git log --oneline --all -- JIM.PostgresData/Migrations/*Change*.cs
```

## Notes

- Scripts are designed to be idempotent where possible (create-if-not-exists)
- All timestamps are relative to NOW() for realistic aging
- Reference attributes properly link to existing MVOs
- Change initiator is always set to SynchronisationRule (type 2) for consistency
