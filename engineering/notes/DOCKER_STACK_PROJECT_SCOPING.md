# Docker stack project scoping: why the JIM stack is a host singleton

## Status: Analysed 2026-08-12. Not proceeding. Recorded so we do not redo the analysis, and so the hazard is written down.

The JIM Docker Compose stack cannot be run twice on one host. `docker compose -p <name>` does not isolate it: the compose files name the containers, the network and the volumes globally, so a second stack adopts the first one's resources rather than creating its own. A test run that resets volumes then destroys the first stack's data.

**Decision: not fixing this.** The only thing it buys is running local git worktrees under Claude Code Desktop, which is not the preferred workflow (cloud environments are, being more portable). The change touches every compose file and the whole integration harness, and that complexity is not worth carrying for a workflow we do not want to encourage. The mitigation is procedural instead: **an agent does not run the stack, `jim-build*`, or `Run-IntegrationTests.ps1` on a host where a developer is using JIM.**

Revisit if local multi-instance ever becomes something we actually want, for example running two JIM versions side by side for an upgrade test.

## What prompted it

On 2026-08-11 an agent session ran the Scenario 16 integration suite from a git worktree under `docker compose -p wt-1283`, believing the project name isolated it. The developer's JIM instance, port-forwarded through Visual Studio Code, then could not be signed in to.

The containers that came up carried the developer's names under the agent's project label:

```
$ docker ps -a --format '{{.Names}}\t{{.Label "com.docker.compose.project"}}'
jim.database   wt-1283
jim.keycloak   wt-1283
jim.web        wt-1283
```

And the volumes had been recreated by that run:

```
$ docker volume inspect jim-db-volume
project=wt-1283  created=2026-08-11T19:23:35Z
```

The integration runner's reset step ("Stopping all containers and removing volumes") removed them by their fixed names, whoever created them. The JIM database and the data protection keys were both destroyed and re-seeded, which is what presented as a broken sign-in.

## Why `-p` does not isolate

Compose prefixes resources with the project name only where you have not named them yourself. The compose files name almost everything, in four separate places. Any one of them left in place keeps the stack a singleton.

| Identity | Declared at | Scope | Why |
|---|---|---|---|
| `container_name: jim.web`, `jim.worker`, `jim.scheduler`, `jim.database`, `jim.keycloak` | `docker-compose.yml` 4, 55, 102, 146; `docker-compose.override.yml` 83 | Daemon | Container names are unique across the whole daemon. Setting this opts the service out of project prefixing entirely. |
| `name: jim-network` | `docker-compose.yml` 188 | Daemon | An explicit `name:` declares this is *the* network, not this project's network. |
| `name: jim-db-volume`, `jim-logs-volume`, `jim-keys-volume`, `jim-connector-files-volume` | `docker-compose.yml` 191-204 | Daemon | Same opt-out. This is the one that cost data: a reset removes them by fixed name. |
| `"5200:80"`, `"5432:5432"`, Keycloak `8181` | `docker-compose.override.yml` 31, 75 | Host | Host ports are a single namespace per machine. Even with everything else fixed, two stacks would contend for these. |

## What the fix would have been

Remove the opt-outs and let the project name do the work it was designed for.

```yaml
# docker-compose.yml
 services:
   jim.web:
-    container_name: jim.web

 networks:
   jim-network:
-    name: jim-network

 volumes:
   jim-db-volume:
-    name: jim-db-volume
```

Parameterise the host ports:

```yaml
# docker-compose.override.yml
   jim.web:
     ports:
-      - "5200:80"
+      - "${JIM_WEB_PORT:-5200}:80"
```

Then each stack declares its own identity in `.env`:

```bash
COMPOSE_PROJECT_NAME=jim
JIM_WEB_PORT=5200
JIM_DB_PORT=5432
JIM_KEYCLOAK_PORT=8181
```

Resources then become `jim-jim.web-1`, `jim_jim-db-volume` and so on, and a second project cannot name, see or delete the first's.

## The finding worth keeping: it is 11 call sites, not 611

The scripts, integration harness and devcontainer docs mention these container names 611 times (`jim.web` 150, `jim.worker` 151, `jim.database` 114, `jim.scheduler` 101, `jim.keycloak` 95). That count is misleading, and it is the main reason this looked more expensive than it is.

Compose registers every service under its **service name** as an alias on the project network, and does so whether or not `container_name` is set. So in-network addressing is completely unaffected: `JIM_DB_HOSTNAME=jim.database`, `JIM_SSO_AUTHORITY=http://jim.keycloak:8080/realms/jim` and every `depends_on` keep working unchanged. Roughly 600 of the references are these, plus prose in documentation.

Only host-side addressing breaks, where a command names a container that no longer exists. There are 11: four `docker exec`, six `docker logs`, one `docker inspect`. Each becomes the project-aware form:

```bash
docker exec jim.database psql -U jim -d jim -c "..."     # before
docker compose exec jim.database psql -U jim -d jim -c "..."  # after
```

If this is ever revisited, start from that number rather than the raw grep count.

## Traps found while scoping it

- **The Keycloak public authority embeds the host port.** `JIM_SSO_PUBLIC_AUTHORITY=http://localhost:8181/realms/jim` is browser-facing, so it reaches Keycloak through the published port rather than Docker DNS. It has to move with `JIM_KEYCLOAK_PORT`, or sign-in fails on any non-default stack, presenting exactly like the incident above and sending the next person hunting in the wrong place.
- **Hard-coded `localhost:5200`** in tests, documentation and the PowerShell module would each need the port variable.
- **Existing volumes are unprefixed**, so the change starts the developer instance against an empty database unless the data is migrated once.
- **Images are unaffected**, still named `jim-web` and friends, so a second stack reuses build layers rather than rebuilding.

## The part that is not a compose problem

Scoping the stack would make isolation possible and cheap. It would not stop an agent choosing to run against the developer's project name. The procedural rule in the Status section above is what actually prevents recurrence, and it stands whether or not the compose change is ever made.

One narrower change is worth considering on its own merits even while the rest is parked: **the integration runner's teardown should be `docker compose -p <project> down -v`, never a `docker volume rm` against a fixed name.** That single behaviour is what turned a container name collision into data loss.
