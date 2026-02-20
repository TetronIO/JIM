#!/bin/bash
# JIM Development Aliases
# This file is sourced automatically by .zshrc

# Unset GITHUB_TOKEN to allow gh CLI to use its own authentication with project scopes
unset GITHUB_TOKEN

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

Diagrams:
  jim-diagrams       - Export Structurizr C4 diagrams as SVG

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
  pwsh -NoProfile -Command "Import-Module Pester; \$config = New-PesterConfiguration; \$config.Run.Path = './src/JIM.PowerShell/JIM/Tests'; \$config.Output.Verbosity = 'Detailed'; Invoke-Pester -Configuration \$config" 2>&1 | tee "$pester_log"
  pester_rc=$?

  dotnet_summary=$(grep -E "^(Passed!|Failed!)" "$dotnet_log")
  pester_summary=$(sed 's/\x1b\[[0-9;]*m//g' "$pester_log" | grep -E "^Tests completed|^Tests Passed")
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
alias jim-test-ps='pwsh -NoProfile -Command "Import-Module Pester; \$config = New-PesterConfiguration; \$config.Run.Path = \"./src/JIM.PowerShell/JIM/Tests\"; \$config.Output.Verbosity = \"Detailed\"; Invoke-Pester -Configuration \$config"'
alias jim-clean='dotnet clean JIM.sln && dotnet build JIM.sln'

# Local run aliases - source .env and override DB hostname for local access
alias jim-web='(set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project src/JIM.Web)'
alias jim-worker='(set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project src/JIM.Worker)'
alias jim-scheduler='(set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project src/JIM.Scheduler)'

# Database management
alias jim-migrate='dotnet ef database update --project src/JIM.PostgresData'
alias jim-migration='dotnet ef migrations add --project src/JIM.PostgresData'
alias jim-db='docker compose -f db.yml up -d'
alias jim-db-stop='docker compose -f db.yml down'
alias jim-db-logs='docker compose -f db.yml logs -f'

# Docker stack management
alias jim-stack='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d'
alias jim-stack-logs='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db logs -f'
alias jim-stack-down='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db down && docker compose -f docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 down --remove-orphans 2>/dev/null || true && docker rm -f samba-ad-primary samba-ad-source samba-ad-target 2>/dev/null || true'
alias jim-restart='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db down && docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d --force-recreate'

# Docker builds (rebuild and start services)
alias jim-build='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d --build'
alias jim-build-web='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db build jim.web && docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d jim.web'
alias jim-build-worker='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db build jim.worker && docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d jim.worker'
alias jim-build-scheduler='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db build jim.scheduler && docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db up -d jim.scheduler'

# Reset
alias jim-reset='docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db down --rmi local --volumes && docker compose -f docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 down --rmi local --volumes --remove-orphans 2>/dev/null || true && docker rm -f samba-ad-primary samba-ad-source samba-ad-target sqlserver-hris-a oracle-hris-b postgres-target openldap-test mysql-test 2>/dev/null || true && docker volume ls --format "{{.Name}}" | grep jim-integration | xargs -r docker volume rm 2>/dev/null || true && docker volume rm -f jim-db-volume jim-logs-volume 2>/dev/null || true && echo "JIM reset complete. All containers, images, and volumes removed. Run jim-build to rebuild."'

# Structurizr diagram export
jim-diagrams() {
  local repo_root structurizr_dir container_name port
  repo_root="$(git rev-parse --show-toplevel 2>/dev/null || echo '/workspaces/JIM')"
  structurizr_dir="${repo_root}/docs/diagrams/structurizr"
  container_name="jim-structurizr-export"
  port=8085

  # Install Chromium OS dependencies if needed (one-time)
  if ! ldconfig -p 2>/dev/null | grep -q libglib-2.0; then
    echo "Installing Chromium dependencies (one-time)..."
    sudo apt-get update -qq && sudo apt-get install -y -qq \
      libglib2.0-0 libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
      libxkbcommon0 libxcomposite1 libxdamage1 libxrandr2 libgbm1 libpango-1.0-0 \
      libcairo2 libasound2 libxshmfence1 libxfixes3 > /dev/null 2>&1
  fi

  # Install npm dependencies if needed
  if [ ! -d "${structurizr_dir}/node_modules" ]; then
    echo "Installing Puppeteer dependencies..."
    (cd "${structurizr_dir}" && npm install --silent)
  fi

  # Remove any stale container
  docker rm -f "${container_name}" 2>/dev/null

  # Start Structurizr Lite (adrs mount resolves the symlink inside the container)
  echo "Starting Structurizr Lite on port ${port}..."
  docker run -d --name "${container_name}" \
    -p "${port}:8080" \
    -v "${structurizr_dir}:/usr/local/structurizr" \
    -v "${repo_root}/docs/adrs:/usr/local/structurizr/adrs" \
    structurizr/lite > /dev/null

  # Wait for Structurizr Lite to be ready
  echo "Waiting for Structurizr Lite to start..."
  local attempts=0
  while [ $attempts -lt 30 ]; do
    if curl -sf "http://localhost:${port}/workspace/diagrams" > /dev/null 2>&1; then
      break
    fi
    attempts=$((attempts + 1))
    sleep 2
  done

  if [ $attempts -ge 30 ]; then
    echo "ERROR: Structurizr Lite failed to start within 60 seconds."
    docker rm -f "${container_name}" > /dev/null 2>&1
    return 1
  fi

  echo "Structurizr Lite is ready."

  # Remove old images (light, dark, and legacy root-level)
  rm -f "${repo_root}/docs/diagrams/images"/jim-structurizr-1-*.svg
  rm -f "${repo_root}/docs/diagrams/images/light"/jim-structurizr-1-*.svg
  rm -f "${repo_root}/docs/diagrams/images/dark"/jim-structurizr-1-*.svg

  # Export diagrams (light + dark)
  node "${structurizr_dir}/export-diagrams.js" \
    "http://localhost:${port}/workspace/diagrams" \
    "${repo_root}/docs/diagrams/images"
  local export_rc=$?

  # Cleanup
  echo "Stopping Structurizr Lite..."
  docker rm -f "${container_name}" > /dev/null 2>&1

  if [ $export_rc -eq 0 ]; then
    echo ""
    echo "Diagrams exported:"
    echo "  Light mode (docs/diagrams/images/light/):"
    ls -1 "${repo_root}/docs/diagrams/images/light"/jim-structurizr-1-*.svg 2>/dev/null | sed 's|.*/||' | sed 's/^/    /'
    echo "  Dark mode (docs/diagrams/images/dark/):"
    ls -1 "${repo_root}/docs/diagrams/images/dark"/jim-structurizr-1-*.svg 2>/dev/null | sed 's|.*/||' | sed 's/^/    /'
  else
    echo "ERROR: Diagram export failed."
    return 1
  fi
}

# Wipe JIM data (reset to initial state without destroying database)
# Note: Preserves MetaverseObjects with Administrator role assignments
alias jim-wipe='echo "Wiping JIM data..." && docker compose -f db.yml exec -T jim.database psql -U ${JIM_DATABASE_USERNAME:-jim} -d ${JIM_DATABASE_NAME:-jim} -c "BEGIN; CREATE TEMP TABLE admin_mvos AS SELECT \"StaticMembersId\" as \"Id\" FROM \"MetaverseObjectRole\" WHERE \"RolesId\" = (SELECT \"Id\" FROM \"Roles\" WHERE \"Name\" = '\''Administrator'\''); DELETE FROM \"MetaverseObjectChangeAttributeValues\" WHERE \"MetaverseObjectChangeAttributeId\" IN (SELECT moca.\"Id\" FROM \"MetaverseObjectChangeAttributes\" moca JOIN \"MetaverseObjectChanges\" moc ON moca.\"MetaverseObjectChangeId\" = moc.\"Id\" WHERE moc.\"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos)); DELETE FROM \"MetaverseObjectChangeAttributes\" WHERE \"MetaverseObjectChangeId\" IN (SELECT \"Id\" FROM \"MetaverseObjectChanges\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos)); DELETE FROM \"MetaverseObjectChanges\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"ConnectedSystemObjectChangeAttributeValues\"; DELETE FROM \"ConnectedSystemObjectChangeAttributes\"; DELETE FROM \"ConnectedSystemObjectChanges\"; DELETE FROM \"PendingExportAttributeValueChanges\"; DELETE FROM \"PendingExports\"; DELETE FROM \"DeferredReferences\"; DELETE FROM \"ActivityRunProfileExecutionItems\"; DELETE FROM \"Activities\"; DELETE FROM \"WorkerTasks\"; DELETE FROM \"MetaverseObjectAttributeValues\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos) AND \"ReferenceValueId\" IS NULL; DELETE FROM \"MetaverseObjectAttributeValues\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"MetaverseObjects\" WHERE \"Id\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"ConnectedSystemObjectAttributeValues\"; DELETE FROM \"ConnectedSystemObjects\"; DELETE FROM \"SyncRuleMappingSourceParamValues\"; DELETE FROM \"SyncRuleMappingSources\"; DELETE FROM \"SyncRuleMappings\"; DELETE FROM \"SyncRuleScopingCriteria\"; DELETE FROM \"SyncRuleScopingCriteriaGroups\"; DELETE FROM \"ObjectMatchingRuleSourceParamValues\"; DELETE FROM \"ObjectMatchingRuleSources\"; DELETE FROM \"ObjectMatchingRules\"; DELETE FROM \"SyncRules\"; DELETE FROM \"ConnectedSystemRunProfiles\"; DELETE FROM \"ConnectedSystemSettingValues\"; DELETE FROM \"ConnectedSystemAttributes\"; DELETE FROM \"ConnectedSystemObjectTypes\"; DELETE FROM \"ConnectedSystemContainers\"; DELETE FROM \"ConnectedSystemPartitions\"; DELETE FROM \"ConnectedSystems\"; COMMIT;" > /dev/null 2>&1 && docker compose -f db.yml exec -T jim.database psql -U ${JIM_DATABASE_USERNAME:-jim} -d ${JIM_DATABASE_NAME:-jim} -c "VACUUM ANALYZE;" > /dev/null 2>&1 && echo "✓ JIM data wiped successfully (preserved admin users)"'
