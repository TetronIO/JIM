---
title: Your First Synchronisation
description: Synchronise 25 people from an HR file, through the Metaverse, into a staff directory file, in the portal or with PowerShell.
type: tutorial
audience: implementer
scenario: 027-FirstSynchronisation
validated: v0.17.0
---

# Your First Synchronisation

In about 30 minutes, you take 25 people from an HR system's file, bring them into JIM, and have JIM write them out to a staff directory. By the end, JIM holds a Metaverse Object for each person, and a second file, written by JIM, lists all 25.

```jim-diagram
archetype: flow
title: Twenty-five people, from the HR file to the staff directory
desc: "The HR System's file, hr-people.csv, holds 25 people. The Full Import (step 7) reads it into the HR System's Connector Space as 25 Connected System Objects. The Full Synchronisation (step 8) applies both Synchronisation Rules: it projects 25 Metaverse Objects and stages 25 Pending Exports for the staff directory. The Export (step 9) writes each Pending Export to the Staff Directory's file, staff-directory.csv, which then lists all 25 people."
in: {label: HR System, note: "`hr-people.csv`"}
out: {label: Staff Directory, note: "`staff-directory.csv`"}
container: {label: JIM}
steps:
- {id: import, label: Full Import, badge: 7, sub: 25 Connected System Objects}
- {id: sync, label: Full Synchronisation, badge: 8, sub: 25 Metaverse Objects, note: 25 Pending Exports staged}
- {id: export, label: Export, badge: 9, sub: writes all 25}
```

Both systems are files, read and written by the JIM File Connector, so you need nothing but JIM itself: this works on an evaluation installation with no network access. Every step is shown in the portal and in PowerShell; follow either, or switch between them.

## :material-clipboard-list-outline: Before you begin

- **JIM is installed and running,** and you can sign in to the portal as an administrator. [Quick Start](quickstart.md) covers installing it.
- **For the PowerShell steps,** the JIM PowerShell module is installed and connected to your JIM, as [PowerShell Module](quickstart.md#powershell-module) describes:

    ```powershell
    Connect-JIM -Url https://jim.example.com
    ```

- **You can run commands on the server JIM runs on,** to copy the two sample files into JIM.
- **You have downloaded the two sample files:** [hr-people.csv](../assets/tutorial/hr-people.csv), the HR system's 25 people, and [staff-directory.csv](../assets/tutorial/staff-directory.csv), the staff directory JIM writes to, which holds only its column headings.

## :material-folder-outline: Step 1: Put the sample files where JIM can read them

The File Connector reads and writes files under `/connector-files`, a folder in JIM's own storage. From the folder you downloaded the files to, copy them into a `tutorial` folder there:

=== "Docker"

    ```bash
    docker exec -u app jim.worker mkdir -p /connector-files/tutorial
    docker exec -i -u app jim.worker sh -c 'cat > /connector-files/tutorial/hr-people.csv' < hr-people.csv
    docker exec -i -u app jim.worker sh -c 'cat > /connector-files/tutorial/staff-directory.csv' < staff-directory.csv
    ```

=== "Podman"

    ```bash
    sudo podman exec jim-worker mkdir -p /connector-files/tutorial
    sudo podman exec -i jim-worker sh -c 'cat > /connector-files/tutorial/hr-people.csv' < hr-people.csv
    sudo podman exec -i jim-worker sh -c 'cat > /connector-files/tutorial/staff-directory.csv' < staff-directory.csv
    ```

## :material-file-document-outline: Step 2: Connect JIM to the HR system

A Connected System is one system JIM exchanges data with. Create one for the HR file, say where the file is, and choose which of its columns JIM manages. Each person is identified by their `employeeId`, which becomes the External ID.

=== "Portal"

    1. Select **Admin**, then **Connected Systems**, then **Create Connected System**.
    2. Choose `JIM File Connector` as the **Connector**, enter `HR System` as the **Name**, and select **Create Connected System**. When JIM asks for a reason for the change, select **Create**; a reason is optional.
    3. On the **Settings** tab, select the folder icon at the end of **File Path**, open the `tutorial` folder, select `hr-people.csv`, and choose **Select**.
    4. Enter `employee` as the **Object Type**, leave **Mode** at **Import Only**, and select **Save Settings**. If JIM asks you to confirm the change, select **Save changes**; then select **Save**.
    5. On the **Schema** tab, select **Retrieve Schema**. JIM reads the file's column headings and finds one Object Type, `employee`, which it selects for you.
    6. Select the `employee` tab, and turn on the switch in the **Selected** column for all seven attributes.
    7. Choose `employeeId` as the **External ID Attribute**, then select **Save Changes**, and **Save changes** if JIM asks you to confirm.

=== "PowerShell"

    ```powershell
    $fileConnector = Get-JIMConnectorDefinition | Where-Object Name -eq 'JIM File Connector'
    $setting = @{}
    (Get-JIMConnectorDefinition -Id $fileConnector.Id).Settings | ForEach-Object { $setting[$_.Name] = $_.Id }

    $hrSystem = New-JIMConnectedSystem -Name 'HR System' -ConnectorDefinitionId $fileConnector.Id -PassThru
    Set-JIMConnectedSystem -Id $hrSystem.Id -SettingValues @{
        $setting['File Path']   = @{ stringValue = '/connector-files/tutorial/hr-people.csv' }
        $setting['Object Type'] = @{ stringValue = 'employee' }
    } | Out-Null

    Import-JIMConnectedSystemSchema -Id $hrSystem.Id | Out-Null
    $hrEmployee = Get-JIMConnectedSystem -Id $hrSystem.Id -ObjectTypes | Where-Object Name -eq 'employee'
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.Id -ObjectTypeId $hrEmployee.Id -Selected $true | Out-Null

    $selection = @{}
    $hrEmployee.Attributes | ForEach-Object { $selection[$_.Id] = @{ selected = $true; isExternalId = ($_.Name -eq 'employeeId') } }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $hrSystem.Id -ObjectTypeId $hrEmployee.Id -AttributeUpdates $selection | Out-Null
    ```

## :material-server-network-outline: Step 3: Connect JIM to the staff directory

Create a second Connected System for the staff directory file. JIM only writes to this one, so its **Mode** is **Export Only**: JIM writes each change to the file and treats it as confirmed, without reading the file back.

=== "Portal"

    1. Select **Admin**, then **Connected Systems**, then **Create Connected System**.
    2. Choose `JIM File Connector` as the **Connector**, enter `Staff Directory` as the **Name**, and select **Create Connected System**, then **Create**.
    3. On the **Settings** tab, choose **Export Only** as the **Mode** first. Then select the folder icon at the end of **File Path**, open the `tutorial` folder, select `staff-directory.csv`, and choose **Select**.
    4. Enter `employee` as the **Object Type**, and select **Save Settings**, confirming and saving as before.
    5. On the **Schema** tab, select **Retrieve Schema**, then the `employee` tab, and turn on the switch in the **Selected** column for all five attributes.
    6. Choose `employeeId` as the **External ID Attribute**, and select **Save Changes**.

=== "PowerShell"

    ```powershell
    $directory = New-JIMConnectedSystem -Name 'Staff Directory' -ConnectorDefinitionId $fileConnector.Id -PassThru
    Set-JIMConnectedSystem -Id $directory.Id -SettingValues @{
        $setting['File Path']   = @{ stringValue = '/connector-files/tutorial/staff-directory.csv' }
        $setting['Object Type'] = @{ stringValue = 'employee' }
        $setting['Mode']        = @{ stringValue = 'Export Only' }
    } | Out-Null

    Import-JIMConnectedSystemSchema -Id $directory.Id | Out-Null
    $directoryEmployee = Get-JIMConnectedSystem -Id $directory.Id -ObjectTypes | Where-Object Name -eq 'employee'
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $directory.Id -ObjectTypeId $directoryEmployee.Id -Selected $true | Out-Null

    $selection = @{}
    $directoryEmployee.Attributes | ForEach-Object { $selection[$_.Id] = @{ selected = $true; isExternalId = ($_.Name -eq 'employeeId') } }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $directory.Id -ObjectTypeId $directoryEmployee.Id -AttributeUpdates $selection | Out-Null
    ```

## :material-hub-outline: Step 4: Bring the HR system's people into the Metaverse

First, tell JIM how to recognise a person it already holds. An Object Matching Rule on the HR System says that an HR record and a Metaverse Object are the same person when the record's `employeeId` equals the Metaverse Object's Employee ID. On this first run nobody matches, but run the steps again and JIM finds the people it already has, rather than creating them twice.

Then create a Synchronisation Rule, which says how data flows between a Connected System and the Metaverse, JIM's own record of each identity. This one is an import rule: it creates a Metaverse Object of type User for each person JIM does not already hold (projection), and copies seven of their attributes across (Attribute Flow).

| HR file column | Metaverse attribute |
|----------------|---------------------|
| `employeeId`   | Employee ID         |
| `firstName`    | First Name          |
| `lastName`     | Last Name           |
| `displayName`  | Display Name        |
| `email`        | Email               |
| `department`   | Department          |
| `jobTitle`     | Job Title           |

=== "Portal"

    1. Open the `HR System` Connected System (**Admin**, then **Connected Systems**), and select its **Matching** tab. Under **Add Matching Rule**, choose `employeeId` as the **Connected System Attribute**, `Employee ID` as the **Metaverse Attribute** and **User** as the **Metaverse Object Type**, then select **Add Matching Rule**.
    2. Select **Admin**, then **Synchronisation Rules**, then **Create Synchronisation Rule**.
    3. Enter `HR System: import people` as the **Name**, and choose `HR System` as the **Connected System**, **Import** as the **Direction**, **User** as the **Metaverse Object Type** and `employee` as the **Connected System Object Type**.
    4. Turn on **Project Users to the Metaverse?**. <!-- conformance: allow TUT-2 "the portal builds this switch's label from the Metaverse Object Type's plural name" -->
    5. On the **Attribute Flow** tab, select **Add Attribute Flow**. Choose **Attribute** as the **Source Type**, `employeeId` as the **Connected System Attribute** and `Employee ID` as the **Metaverse Attribute**, then select **Add Attribute Flow**.
    6. Add an Attribute Flow in the same way for each of the table's other six rows.
    7. Select **Create Synchronisation Rule**, then **Create**.

=== "PowerShell"

    ```powershell
    $mvUser = Get-JIMMetaverseObjectType | Where-Object Name -eq 'User'
    $mvAttribute = @{}; Get-JIMMetaverseAttribute | ForEach-Object { $mvAttribute[$_.Name] = $_.Id }
    $hrAttribute = @{}; $hrEmployee.Attributes | ForEach-Object { $hrAttribute[$_.Name] = $_.Id }

    New-JIMMatchingRule -ConnectedSystemId $hrSystem.Id -ObjectTypeId $hrEmployee.Id -MetaverseObjectTypeId $mvUser.Id `
        -SourceAttributeId $hrAttribute['employeeId'] -TargetMetaverseAttributeId $mvAttribute['Employee ID'] | Out-Null

    $importRule = New-JIMSyncRule -Name 'HR System: import people' -ConnectedSystemId $hrSystem.Id `
        -ConnectedSystemObjectTypeId $hrEmployee.Id -MetaverseObjectTypeId $mvUser.Id -Direction Import -ProjectToMetaverse -PassThru

    $flows = [ordered]@{
        employeeId = 'Employee ID'; firstName = 'First Name'; lastName = 'Last Name'; displayName = 'Display Name'
        email = 'Email'; department = 'Department'; jobTitle = 'Job Title'
    }
    foreach ($column in $flows.Keys) {
        New-JIMSyncRuleMapping -SyncRuleId $importRule.Id -SourceConnectedSystemAttributeId $hrAttribute[$column] `
            -TargetMetaverseAttributeId $mvAttribute[$flows[$column]] | Out-Null
    }
    ```

## :material-export: Step 5: Send the people to the staff directory

The second Synchronisation Rule is an export rule: for each User in the Metaverse, it creates an entry in the staff directory (provisioning), and writes five of the person's attributes to it.

It has a scope, too. Your JIM most likely holds one User already: you, the administrator who signed in. You have no Employee ID, and the staff directory is for the HR system's people, whose Employee IDs all start with `HR-`, so the rule takes only those.

| Metaverse attribute | Staff directory column |
|---------------------|------------------------|
| Employee ID         | `employeeId`           |
| Display Name        | `displayName`          |
| Email               | `email`                |
| Department          | `department`           |
| Job Title           | `jobTitle`             |

=== "Portal"

    1. Select **Admin**, then **Synchronisation Rules**, then **Create Synchronisation Rule**.
    2. Enter `Staff Directory: provision people` as the **Name**, and choose `Staff Directory` as the **Connected System**, **Export** as the **Direction**, **User** as the **Metaverse Object Type** and `employee` as the **Connected System Object Type**.
    3. Turn on **Provision employees to the Connected System?**. <!-- conformance: allow TUT-2 "the portal builds this switch's label from the Connected System Object Type's name" -->
    4. On the **Attribute Flow** tab, add an Attribute Flow for each row of the table, as in Step 4: the **Metaverse Attribute** is the source, and the **Connected System Attribute** the target.
    5. On the **Scope** tab, choose **Filtered**, then **Add Criteria Group**. Choose **All** as the group type, and select **Add Group**.
    6. Select **Add Criteria**. Choose `Employee ID` as the **Metaverse Attribute** and **starts with** as the **Comparison Type**, enter `HR-` as the **Text Value**, and select **Add Criteria**.
    7. Select **Create Synchronisation Rule**, then **Create**.

=== "PowerShell"

    ```powershell
    $directoryAttribute = @{}; $directoryEmployee.Attributes | ForEach-Object { $directoryAttribute[$_.Name] = $_.Id }

    $exportRule = New-JIMSyncRule -Name 'Staff Directory: provision people' -ConnectedSystemId $directory.Id `
        -ConnectedSystemObjectTypeId $directoryEmployee.Id -MetaverseObjectTypeId $mvUser.Id -Direction Export -ProvisionToConnectedSystem -PassThru

    $flows = [ordered]@{
        'Employee ID' = 'employeeId'; 'Display Name' = 'displayName'; 'Email' = 'email'; 'Department' = 'department'; 'Job Title' = 'jobTitle'
    }
    foreach ($attribute in $flows.Keys) {
        New-JIMSyncRuleMapping -SyncRuleId $exportRule.Id -SourceMetaverseAttributeId $mvAttribute[$attribute] `
            -TargetConnectedSystemAttributeId $directoryAttribute[$flows[$attribute]] | Out-Null
    }

    $group = New-JIMScopingCriteriaGroup -SyncRuleId $exportRule.Id -Type All -PassThru
    New-JIMScopingCriterion -SyncRuleId $exportRule.Id -GroupId $group.Id -MetaverseAttributeName 'Employee ID' `
        -ComparisonType StartsWith -StringValue 'HR-' | Out-Null
    ```

## :material-play-circle-outline: Step 6: Create the Run Profiles

A Run Profile is one operation JIM runs against a Connected System. You need three: a Full Import to read the HR file, a Full Synchronisation to apply the rules, and an Export to write the staff directory. Each one reads or writes the file its Connected System names: leave the Full Import's **File Path** empty, and the other two have none.

=== "Portal"

    1. Open the `HR System` Connected System (**Admin**, then **Connected Systems**), and select its **Run Profiles** tab.
    2. Select **Create Run Profile**. Enter `Full Import` as the **Name**, choose **Full Import** as the **Run Type**, and select **Create Run Profile**.
    3. Select **Create Run Profile** again. Enter `Full Synchronisation` as the **Name**, choose **Full Synchronisation** as the **Run Type**, and select **Create Run Profile**.
    4. Open the `Staff Directory` Connected System's **Run Profiles** tab, and create a Run Profile named `Export` with **Export** as its **Run Type**.

=== "PowerShell"

    ```powershell
    New-JIMRunProfile -ConnectedSystemName 'HR System' -Name 'Full Import' -RunType FullImport | Out-Null
    New-JIMRunProfile -ConnectedSystemName 'HR System' -Name 'Full Synchronisation' -RunType FullSynchronisation | Out-Null
    New-JIMRunProfile -ConnectedSystemName 'Staff Directory' -Name 'Export' -RunType Export | Out-Null
    ```

## :material-import: Step 7: Import the HR file

Run the Full Import. JIM reads the file and holds each row as a Connected System Object in the HR System's Connector Space, its own copy of what the HR system says.

=== "Portal"

    1. Select **Admin**, then **Operations**. On the **Activity** tab, select **Queue a Run Profile**, choose `HR System` as the **Connected System** and `Full Import` as the **Run Profile**, then select **Queue**.
    2. When the run finishes it moves to **Completed**; select it, then **View Full Details** to open its Activity. **CSOs Added** reads 25. <!-- conformance: allow LANG-4 "the label the portal shows" -->

=== "PowerShell"

    ```powershell
    $import = Start-JIMRunProfile -ConnectedSystemName 'HR System' -RunProfileName 'Full Import' -Wait -PassThru
    Get-JIMConnectedSystemObject -ConnectedSystemId $hrSystem.Id -Count
    ```

<!-- checkpoint: (Get-JIMConnectedSystemObject -ConnectedSystemId $hrSystem.Id -Count) -eq 25 -->
The HR System's Connector Space holds 25 Connected System Objects, which its **Connector Space** button counts.

## :material-sync: Step 8: Synchronise

Run the Full Synchronisation. JIM applies both Synchronisation Rules: the import rule projects each person into the Metaverse, and the export rule finds that each of them belongs in the staff directory, so it stages an entry for each as a Pending Export.

=== "Portal"

    1. On the **Activity** tab, queue the `HR System` Connected System's `Full Synchronisation` Run Profile, and open its Activity when it finishes. **MVOs Projected** reads 25, and so does **CSOs Provisioned**. <!-- conformance: allow LANG-4 "the label the portal shows" -->
    2. Select **Users**, then **All Users**: Amara Okafor and the other 24 people are there, with the attributes the import rule flowed.

=== "PowerShell"

    ```powershell
    $sync = Start-JIMRunProfile -ConnectedSystemName 'HR System' -RunProfileName 'Full Synchronisation' -Wait -PassThru
    (Get-JIMActivityStats -Id $sync.ActivityId).TotalProjections
    Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Employee ID' -AttributeValue 'HR-1001'
    ```

<!-- checkpoint: (Get-JIMActivityStats -Id $sync.ActivityId).TotalProjections -eq 25 -->
<!-- checkpoint: (Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Employee ID' -AttributeValue 'HR-1001').DisplayName -eq 'Amara Okafor' -->
<!-- checkpoint: (Get-JIMPendingExport -ConnectedSystemId $directory.Id -Count) -eq 25 -->
JIM has projected 25 Metaverse Objects, Amara Okafor (`HR-1001`) among them, and the Staff Directory has 25 Pending Exports waiting, which its **Pending Exports** button counts. Nothing has been written to the staff directory yet.

## :material-flag-checkered: Step 9: Export to the staff directory

Run the Export. JIM writes each Pending Export to the staff directory file.

=== "Portal"

    1. On the **Activity** tab, queue the `Staff Directory` Connected System's `Export` Run Profile, and open its Activity when it finishes. **CSOs Exported** reads 25. <!-- conformance: allow LANG-4 "the label the portal shows" -->

=== "PowerShell"

    ```powershell
    $export = Start-JIMRunProfile -ConnectedSystemName 'Staff Directory' -RunProfileName 'Export' -Wait -PassThru
    Get-JIMPendingExport -ConnectedSystemId $directory.Id -Count
    ```

Then look at the file JIM wrote:

=== "Docker"

    ```bash
    docker exec jim.worker cat /connector-files/tutorial/staff-directory.csv
    ```

=== "Podman"

    ```bash
    sudo podman exec jim-worker cat /connector-files/tutorial/staff-directory.csv
    ```

<!-- checkpoint: (Get-JIMPendingExport -ConnectedSystemId $directory.Id -Count) -eq 0 -->
<!-- checkpoint-file: /connector-files/tutorial/staff-directory.csv rows=25 -->
The Staff Directory has no Pending Exports left, and its file lists all 25 people, one row each, with the five columns the export rule writes.

## :material-trophy-outline: What you achieved

You connected JIM to two systems and synchronised 25 identities from one to the other, through the Metaverse:

- **An import** read the HR file into the HR System's Connector Space.
- **A synchronisation** projected each person into the Metaverse, flowed their attributes, and staged an export for each to the staff directory. Its Object Matching Rule means a later run finds those people rather than creating them again.
- **An export** wrote them to the staff directory file.

Every run recorded an Activity, which is the place to look when a run does not go as expected; [Troubleshooting Synchronisation](../administration/troubleshooting-synchronisation.md) explains each warning and error. To go further:

- [Synchronisation Pipeline](../concepts/synchronisation-pipeline.md) explains how import, synchronisation and export fit together, and the order a change needs them in.
- [Synchronisation Rules](../configuration/synchronisation-rules.md) covers Object Matching Rules, scoping and expressions in depth.
- [Connectors](../connectors/index.md) lists the systems JIM connects to, directories and databases among them.
- [Schedules](../configuration/schedules.md) run the steps you ran by hand, on a timetable.
