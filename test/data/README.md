# JIM Test Data

This directory contains SQL scripts for seeding test data into JIM databases for development and UI testing.

## Available Scripts

### seed-change-history-simple.sql

Generates realistic change history data for testing the Change History UI feature.

**What it creates:**
- 2 User MVOs: Alice Anderson and Bob Brown with extensive change history
- 1 Group MVO: Software Engineers with membership changes
- ~9 total change records covering all scenarios:
  - Promotions and job title changes
  - Department and email updates
  - Manager reference changes (Bob reports to Alice)
  - Group membership changes
  - Uses built-in User and Group types with built-in attributes

**How to run:**

```bash
# Option 1: Pipe from host (recommended - works in devcontainer)
docker compose exec -T jim.database psql -U jim -d jim < test/data/seed-change-history-simple.sql

# Option 2: Copy into container then run
docker cp test/data/seed-change-history-simple.sql jim.database:/tmp/
docker compose exec jim.database psql -U jim -d jim -f /tmp/seed-change-history-simple.sql

# Option 3: From host machine (if PostgreSQL client installed locally)
psql -h localhost -p 5432 -U jim -d jim -f test/data/seed-change-history-simple.sql
```

**Requirements:**
- JIM must be running (`jim-stack` or `jim-stack-dev`)
- Database must have built-in types initialized (User, Group types with built-in attributes)
- Script uses existing built-in types and creates test MVOs with change history

**After running:**
The script will output URLs like:
```
UI Testing URLs:
  Alice: http://localhost:5200/t/users/v/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
  Bob: http://localhost:5200/t/users/v/yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy
  Engineers: http://localhost:5200/t/groups/v/zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz
```

Copy these URLs and navigate to them in your browser to view the change history timeline. Alice and Bob will also appear in the Users list in the navigation.


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
