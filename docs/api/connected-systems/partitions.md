---
title: Partitions and Containers
---

# Partitions and Containers

Partitions and containers represent the organisational hierarchy of the external identity store, such as LDAP partitions and organisational units. After [importing the hierarchy](import-hierarchy.md), you can select which partitions and containers to include in synchronisation scope.

---

## List Partitions

Returns all partitions for a connected system, including their nested container hierarchy.

```
GET /api/v1/synchronisation/connected-systems/{connectedSystemId}/partitions
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |

### Examples

=== "curl"

    ```bash
    curl https://jim.example.com/api/v1/synchronisation/connected-systems/1/partitions \
      -H "X-Api-Key: jim_xxxxxxxxxxxx"
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Get-JIMConnectedSystemPartition -ConnectedSystemId 1
    ```

### Response

Returns `200 OK` with an array of partitions.

```json
[
  {
    "id": 1,
    "name": "DC=example,DC=com",
    "externalId": "DC=example,DC=com",
    "selected": true,
    "connectedSystemId": 1,
    "containers": [
      {
        "id": 10,
        "name": "Users",
        "externalId": "OU=Users,DC=example,DC=com",
        "description": null,
        "hidden": false,
        "selected": true,
        "partitionId": 1,
        "connectedSystemId": 1,
        "childContainers": [
          {
            "id": 11,
            "name": "Engineering",
            "externalId": "OU=Engineering,OU=Users,DC=example,DC=com",
            "description": null,
            "hidden": false,
            "selected": true,
            "partitionId": 1,
            "connectedSystemId": 1,
            "childContainers": []
          }
        ]
      }
    ]
  }
]
```

### Partition Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Partition name |
| `externalId` | string | External identifier in the source system |
| `selected` | boolean | Whether this partition is included in synchronisation scope |
| `connectedSystemId` | integer | Parent connected system ID |
| `containers` | array | Nested container hierarchy |

### Container Attributes

| Field | Type | Description |
|-------|------|-------------|
| `id` | integer | Unique identifier |
| `name` | string | Container name |
| `externalId` | string | External identifier (e.g. distinguished name) |
| `description` | string, nullable | Optional description |
| `hidden` | boolean | Whether this container is hidden by the connector |
| `selected` | boolean | Whether this container is included in synchronisation scope |
| `partitionId` | integer, nullable | Parent partition ID |
| `connectedSystemId` | integer, nullable | Parent connected system ID |
| `childContainers` | array | Nested child containers (recursive structure) |

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system does not exist |

---

## Update a Partition

Updates the selection state of a partition.

```
PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/partitions/{partitionId}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `partitionId` | integer | ID of the partition |

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `selected` | boolean | No | Include this partition in synchronisation scope |

### Examples

=== "curl"

    ```bash
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1/partitions/1 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{ "selected": true }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Set-JIMConnectedSystemPartition -ConnectedSystemId 1 -PartitionId 1 -Selected $true
    ```

### Response

Returns `200 OK` with the updated partition object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or partition does not exist |

---

## Update a Container

Updates the selection state of a container.

```
PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/containers/{containerId}
```

### Path Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `connectedSystemId` | integer | ID of the connected system |
| `containerId` | integer | ID of the container |

### Request Body

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `selected` | boolean | No | Include this container in synchronisation scope |

### Examples

=== "curl"

    ```bash
    curl -X PUT https://jim.example.com/api/v1/synchronisation/connected-systems/1/containers/10 \
      -H "X-Api-Key: jim_xxxxxxxxxxxx" \
      -H "Content-Type: application/json" \
      -d '{ "selected": true }'
    ```

=== "PowerShell"

    ```powershell
    Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

    Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Selected $true
    ```

### Response

Returns `200 OK` with the updated container object.

### Errors

| Status | Code | Description |
|--------|------|-------------|
| `401` | `UNAUTHORISED` | Authentication required |
| `403` | `FORBIDDEN` | Insufficient permissions (Administrator role required) |
| `404` | `NOT_FOUND` | Connected system or container does not exist |
