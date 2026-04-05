---
title: Manage Steps
---

# Manage Steps

Schedule steps are managed through the [Create](create.md) and [Update](update.md) endpoints. The API treats steps as part of the schedule object; the `steps` array in an update request **replaces all existing steps**.

The JIM PowerShell module provides dedicated cmdlets for incremental step management, which handle the read-modify-write cycle automatically.

---

## Add a Step

Adds a step to an existing schedule. The PowerShell module reads the current steps, appends the new step, and sends the full list as an update.

### Examples

=== "curl"

    ```bash
    # To add a step via the API, first retrieve the schedule to get current steps,
    # then send an update with the full steps array including the new step.
    # See the Update endpoint for the full request format.

    curl -X PUT https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "steps": [
          {
            "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
            "stepIndex": 0,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 1
          },
          {
            "stepIndex": 1,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 2
          }
        ]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Add a sequential step by connected system and run profile name
    Add-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -StepType RunProfile `
        -ConnectedSystemName "Corporate LDAP" `
        -RunProfileName "Delta Import"

    # Add a parallel step (runs alongside the previous step)
    Add-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -StepType RunProfile `
        -ConnectedSystemName "HR Database" `
        -RunProfileName "Delta Import" `
        -Parallel

    # Add a step by ID with continue-on-failure
    Add-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -StepType RunProfile `
        -ConnectedSystemId 1 -RunProfileId 2 `
        -ContinueOnFailure
    ```

---

## Remove a Step

Removes a step from an existing schedule by its index. Remaining step indices are renumbered automatically.

### Examples

=== "curl"

    ```bash
    # To remove a step via the API, retrieve the schedule, remove the step from
    # the steps array, and send an update with the remaining steps.

    curl -X PUT https://jim.example.com/api/v1/schedules/a1b2c3d4-e5f6-7890-abcd-ef1234567890 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{
        "steps": [
          {
            "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
            "stepIndex": 0,
            "stepType": "RunProfile",
            "connectedSystemId": 1,
            "runProfileId": 1
          }
        ]
      }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    # Remove step at index 2
    Remove-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -StepIndex 2

    # Remove without confirmation
    Remove-JIMScheduleStep -ScheduleId "a1b2c3d4-e5f6-7890-abcd-ef1234567890" `
        -StepIndex 0 -Force
    ```
