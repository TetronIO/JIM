#!/bin/bash
# JIM Development Aliases
# This file is sourced automatically by .zshrc

# Unset GITHUB_TOKEN to allow gh CLI to use its own authentication with project scopes
unset GITHUB_TOKEN

# Compose file variables for cleaner aliases
JIM_COMPOSE="docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db"
JIM_COMPOSE_DEV="docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml -f docker-compose.dev-tools.yml --profile with-db"

# Help - list all jim aliases
alias jim='echo "JIM Development Aliases:

.NET Local Development:
  jim-compile        - dotnet build JIM.sln
  jim-test           - Run unit + workflow tests (excludes Explicit)
  jim-test-all       - Run ALL tests (incl. Explicit + Pester)
  jim-test-ps        - Run PowerShell Pester tests
  jim-clean          - dotnet clean && build
  jim-web            - Run JIM.Web locally (sources .env)
  jim-worker         - Run JIM.Worker locally (sources .env)
  jim-scheduler      - Run JIM.Scheduler locally (sources .env)

Database Management:
  jim-migrate        - dotnet ef database update
  jim-migration      - dotnet ef migrations add
  jim-db             - Start PostgreSQL
  jim-db-stop        - Stop PostgreSQL
  jim-db-logs        - View database logs

Docker Stack Management:
  jim-stack          - Start Docker stack
  jim-stack-logs     - View Docker stack logs
  jim-stack-down     - Stop Docker stack
  jim-restart        - Recreate stack (re-reads .env, no rebuild)

Docker Builds (rebuild + start):
  jim-build          - Rebuild all services + start
  jim-build-web      - Rebuild jim.web + start
  jim-build-worker   - Rebuild jim.worker + start
  jim-build-scheduler - Rebuild jim.scheduler + start

Reset:
  jim-reset          - Full reset (containers, images, volumes)
  jim-wipe           - Wipe JIM data (reset CSOs/MVOs/config, keep schema)

Help:
  jim                - Show this help message
"'

# .NET local development
alias jim-compile='dotnet build JIM.sln'
alias jim-test='dotnet test JIM.sln'
jim-test-all() {
  local dotnet_log pester_log dotnet_rc pester_rc dotnet_summary pester_summary
  dotnet_log=$(mktemp)
  pester_log=$(mktemp)

  echo "=== Running .NET tests (including Explicit) ==="
  dotnet test JIM.sln --settings test/run-all.runsettings 2>&1 | tee "$dotnet_log"
  dotnet_rc=$?

  echo ""
  echo "=== Running Pester tests ==="
  pwsh -NoProfile -Command "Import-Module Pester; \$config = New-PesterConfiguration; \$config.Run.Path = './JIM.PowerShell/JIM/Tests'; \$config.Output.Verbosity = 'Detailed'; Invoke-Pester -Configuration \$config" 2>&1 | tee "$pester_log"
  pester_rc=$?

  dotnet_summary=$(grep -E "^(Passed!|Failed!)" "$dotnet_log")
  pester_summary=$(grep -E "^Tests completed|^(Passed|Failed)" "$pester_log")
  rm -f "$dotnet_log" "$pester_log"

  echo ""
  echo "========================================"
  echo "         TEST RESULTS SUMMARY"
  echo "========================================"
  echo ""
  echo ".NET Tests (dotnet test):"
  if [ -n "$dotnet_summary" ]; then
    echo "$dotnet_summary" | sed 's/^/  /'
  else
    echo "  No summary available (exit code: $dotnet_rc)"
  fi
  echo ""
  echo "Pester Tests:"
  if [ -n "$pester_summary" ]; then
    echo "$pester_summary" | sed 's/^/  /'
  else
    echo "  No summary available (exit code: $pester_rc)"
  fi
  echo ""
  if [ "$dotnet_rc" -eq 0 ] && [ "$pester_rc" -eq 0 ]; then
    echo "Overall: ALL TESTS PASSED"
  else
    echo "Overall: SOME TESTS FAILED"
    [ "$dotnet_rc" -ne 0 ] && echo "  - .NET tests failed (exit code: $dotnet_rc)"
    [ "$pester_rc" -ne 0 ] && echo "  - Pester tests failed (exit code: $pester_rc)"
  fi
  echo "========================================"

  [ "$dotnet_rc" -ne 0 ] || [ "$pester_rc" -ne 0 ] && return 1
  return 0
}
alias jim-test-ps='pwsh -NoProfile -Command "Import-Module Pester; \$config = New-PesterConfiguration; \$config.Run.Path = \"./JIM.PowerShell/JIM/Tests\"; \$config.Output.Verbosity = \"Detailed\"; Invoke-Pester -Configuration \$config"'
alias jim-clean='dotnet clean JIM.sln && dotnet build JIM.sln'

# Local run aliases - source .env and override DB hostname for local access
alias jim-web='(set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project JIM.Web)'
alias jim-worker='(set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project JIM.Worker)'
alias jim-scheduler='(set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project JIM.Scheduler)'

# Database management
alias jim-migrate='dotnet ef database update --project JIM.PostgresData'
alias jim-migration='dotnet ef migrations add --project JIM.PostgresData'
alias jim-db='docker compose -f db.yml up -d'
alias jim-db-stop='docker compose -f db.yml down'
alias jim-db-logs='docker compose -f db.yml logs -f'

# Docker stack management
alias jim-stack='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d'
alias jim-stack-logs='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db logs -f'
alias jim-stack-down='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db down && docker compose -f docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 down --remove-orphans 2>/dev/null || true && docker rm -f samba-ad-primary samba-ad-source samba-ad-target 2>/dev/null || true'
alias jim-restart='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db down && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d --force-recreate'

# Docker builds (rebuild and start services)
alias jim-build='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d --build'
alias jim-build-web='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.web && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.web'
alias jim-build-worker='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.worker && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.worker'
alias jim-build-scheduler='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.scheduler && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.scheduler'

# Reset
alias jim-reset='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db down --rmi local --volumes && docker compose -f docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 down --rmi local --volumes --remove-orphans 2>/dev/null || true && docker rm -f samba-ad-primary samba-ad-source samba-ad-target sqlserver-hris-a oracle-hris-b postgres-target openldap-test mysql-test 2>/dev/null || true && docker volume ls --format "{{.Name}}" | grep jim-integration | xargs -r docker volume rm 2>/dev/null || true && docker volume rm -f jim-db-volume jim-logs-volume 2>/dev/null || true && echo "JIM reset complete. All containers, images, and volumes removed. Run jim-build to rebuild."'

# Wipe JIM data (reset to initial state without destroying database)
# Note: Preserves MetaverseObjects with Administrator role assignments
alias jim-wipe='echo "Wiping JIM data..." && docker compose -f db.yml exec -T jim.database psql -U ${JIM_DATABASE_USERNAME:-jim} -d ${JIM_DATABASE_NAME:-jim} -c "BEGIN; CREATE TEMP TABLE admin_mvos AS SELECT \"StaticMembersId\" as \"Id\" FROM \"MetaverseObjectRole\" WHERE \"RolesId\" = (SELECT \"Id\" FROM \"Roles\" WHERE \"Name\" = '\''Administrator'\''); DELETE FROM \"MetaverseObjectChangeAttributeValues\" WHERE \"MetaverseObjectChangeAttributeId\" IN (SELECT moca.\"Id\" FROM \"MetaverseObjectChangeAttributes\" moca JOIN \"MetaverseObjectChanges\" moc ON moca.\"MetaverseObjectChangeId\" = moc.\"Id\" WHERE moc.\"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos)); DELETE FROM \"MetaverseObjectChangeAttributes\" WHERE \"MetaverseObjectChangeId\" IN (SELECT \"Id\" FROM \"MetaverseObjectChanges\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos)); DELETE FROM \"MetaverseObjectChanges\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"ConnectedSystemObjectChangeAttributeValues\"; DELETE FROM \"ConnectedSystemObjectChangeAttributes\"; DELETE FROM \"ConnectedSystemObjectChanges\"; DELETE FROM \"PendingExportAttributeValueChanges\"; DELETE FROM \"PendingExports\"; DELETE FROM \"DeferredReferences\"; DELETE FROM \"ActivityRunProfileExecutionItems\"; DELETE FROM \"Activities\"; DELETE FROM \"WorkerTasks\"; DELETE FROM \"MetaverseObjectAttributeValues\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos) AND \"ReferenceValueId\" IS NULL; DELETE FROM \"MetaverseObjectAttributeValues\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"MetaverseObjects\" WHERE \"Id\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"ConnectedSystemObjectAttributeValues\"; DELETE FROM \"ConnectedSystemObjects\"; DELETE FROM \"SyncRuleMappingSourceParamValues\"; DELETE FROM \"SyncRuleMappingSources\"; DELETE FROM \"SyncRuleMappings\"; DELETE FROM \"SyncRuleScopingCriteria\"; DELETE FROM \"SyncRuleScopingCriteriaGroups\"; DELETE FROM \"ObjectMatchingRuleSourceParamValues\"; DELETE FROM \"ObjectMatchingRuleSources\"; DELETE FROM \"ObjectMatchingRules\"; DELETE FROM \"SyncRules\"; DELETE FROM \"ConnectedSystemRunProfiles\"; DELETE FROM \"ConnectedSystemSettingValues\"; DELETE FROM \"ConnectedSystemAttributes\"; DELETE FROM \"ConnectedSystemObjectTypes\"; DELETE FROM \"ConnectedSystemContainers\"; DELETE FROM \"ConnectedSystemPartitions\"; DELETE FROM \"ConnectedSystems\"; COMMIT;" > /dev/null 2>&1 && docker compose -f db.yml exec -T jim.database psql -U ${JIM_DATABASE_USERNAME:-jim} -d ${JIM_DATABASE_NAME:-jim} -c "VACUUM ANALYZE;" > /dev/null 2>&1 && echo "✓ JIM data wiped successfully (preserved admin users)"'
