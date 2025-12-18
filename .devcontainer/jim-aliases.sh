#!/bin/bash
# JIM Development Aliases
# This file is sourced automatically by .zshrc

# Unset GITHUB_TOKEN to allow gh CLI to use its own authentication with project scopes
unset GITHUB_TOKEN

# Enable BuildKit for faster Docker builds with better caching
export DOCKER_BUILDKIT=1

# Compose file variables for cleaner aliases
JIM_COMPOSE="docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db"

# .NET local development
alias jim-build='dotnet build JIM.sln'
alias jim-test='dotnet test JIM.sln'
alias jim-clean='dotnet clean JIM.sln && dotnet build JIM.sln'
alias jim-web='dotnet run --project JIM.Web'
alias jim-api='dotnet run --project JIM.Api'
alias jim-worker='dotnet run --project JIM.Worker'

# Database management
alias jim-migrate='dotnet ef database update --project JIM.PostgresData'
alias jim-migration='dotnet ef migrations add --project JIM.PostgresData'
alias jim-db='docker compose -f db.yml up -d'
alias jim-db-stop='docker compose -f db.yml down'
alias jim-db-logs='docker compose -f db.yml logs -f'
alias jim-adminer='echo "Adminer running at http://localhost:8080"'

# Docker stack management
alias jim-stack='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d'
alias jim-stack-logs='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db logs -f'
alias jim-stack-down='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db down'

# Docker builds (rebuild and start services)
alias jim-build-stack='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d --build'
alias jim-build-web='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.web && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.web'
alias jim-build-worker='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.worker && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.worker'
alias jim-build-scheduler='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db build jim.scheduler && docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db up -d jim.scheduler'

# Reset
alias jim-reset='docker compose -f docker-compose.yml -f docker-compose.override.codespaces.yml --profile with-db down && docker volume rm -f jim-db-volume jim-logs-volume && echo "JIM reset complete. Run jim-build-stack to rebuild and start fresh."'
