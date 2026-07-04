# Integration test results

This directory holds per-run artefacts written by `Run-IntegrationTests.ps1`: scenario
transcripts (`logs/`), docker stats CSVs, docker events logs, volume-audit logs, error-watcher
sentinels and performance metrics. Everything except this file is gitignored.

## Data provenance caveat (#918)

Before 2026-07-04, the docker stats sampler could survive its run (the runner's stop only
reached the .NET global-tool shim, not the sampler process itself), silently appending live
samples to historical CSVs. For any `docker-stats-*.csv` produced before the #918 fix, rows
timestamped after that run's transcript end time are pollution from a leaked sampler and must
not be used for analysis or cross-run comparison. CSVs produced after the fix are trustworthy
end to end; the runner only prints "Docker stats capture stopped" once it has verified no
sampler process for that run survives.
