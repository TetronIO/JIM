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
  jim-test           - dotnet test JIM.sln
  jim-pester         - Run PowerShell Pester tests
  jim-clean          - dotnet clean && build
  jim-web            - dotnet run --project JIM.Web
  jim-worker         - dotnet run --project JIM.Worker

Database Management:
  jim-migrate        - dotnet ef database update
  jim-migration      - dotnet ef migrations add
  jim-db             - Start PostgreSQL + Adminer
  jim-db-stop        - Stop PostgreSQL + Adminer
  jim-db-logs        - View database logs

Docker Stack Management:
  jim-stack          - Start Docker stack (no dev tools)
  jim-stack-dev      - Start Docker stack + Adminer
  jim-stack-logs     - View Docker stack logs
  jim-stack-down     - Stop Docker stack

Docker Builds (rebuild + start):
  jim-build          - Rebuild all services + start
  jim-build-dev      - Rebuild all services + start + Adminer
  jim-build-web      - Rebuild jim.web + start
  jim-build-worker   - Rebuild jim.worker + start
  jim-build-scheduler - Rebuild jim.scheduler + start

Reset:
  jim-reset          - Full reset (containers, images, volumes)

Help:
  jim                - Show this help message
"'

# .NET local development
alias jim-compile='dotnet build JIM.sln'
alias jim-test='dotnet test JIM.sln'
alias jim-pester='pwsh -NoProfile -Command "Import-Module Pester; \$config = New-PesterConfiguration; \$config.Run.Path = \"./JIM.PowerShell/JIM/Tests\"; \$config.Output.Verbosity = \"Detailed\"; Invoke-Pester -Configuration \$config"'
alias jim-clean='dotnet clean JIM.sln && dotnet build JIM.sln'
alias jim-web='dotnet run --project JIM.Web'
alias jim-worker='dotnet run --project JIM.Worker'

# Database management
alias jim-migrate='dotnet ef database update --project JIM.PostgresData'
alias jim-migration='dotnet ef migrations add --project JIM.PostgresData'
alias jim-db='docker compose -f db.yml up -d'
alias jim-db-stop='docker compose -f db.yml down'
alias jim-db-logs='docker compose -f db.yml logs -f'

# Docker stack management (production-like, no dev tools)
alias jim-stack='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d'
alias jim-stack-dev='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml -f docker-compose.dev-tools.yml --profile with-db up -d'
alias jim-stack-logs='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db logs -f'
alias jim-stack-down='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml -f docker-compose.dev-tools.yml --profile with-db down && docker compose -f docker-compose.integration-tests.yml down 2>/dev/null || true'

# Docker builds (rebuild and start services)
alias jim-build='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d --build'
alias jim-build-dev='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml -f docker-compose.dev-tools.yml --profile with-db up -d --build'
alias jim-build-web='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.web && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.web'
alias jim-build-worker='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.worker && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.worker'
alias jim-build-scheduler='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.scheduler && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.scheduler'

# Reset (includes dev-tools compose to clean up adminer if it was used)
alias jim-reset='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml -f docker-compose.dev-tools.yml --profile with-db down --rmi local --volumes && docker compose -f docker-compose.integration-tests.yml down --rmi local --volumes 2>/dev/null || true && docker volume rm -f jim-db-volume jim-logs-volume 2>/dev/null || true && echo "JIM reset complete. All containers, images, and volumes removed. Run jim-build to rebuild."'
