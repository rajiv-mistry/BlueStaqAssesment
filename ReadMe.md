# NotesService Solution

## Overview
NotesService is a .NET 10 solution for team notes management. It provides both REST and gRPC APIs for user authentication, team validation, and note management. The solution includes a console client that interacts with the REST APIs for user login, team verification, and note operations.

`NotesService` is a .NET 10 team-notes solution.
It includes:
- a server project exposing REST APIs (plus existing gRPC services)
- a console client (`NotesGrpcClient`) using REST for login, team verification, and notes

## Projects

### 1) NotesService (`NotesService/NotesService.csproj`)
ASP.NET Core service with REST + gRPC endpoints.

#### REST APIs
- Notes
  - `GET /api/notes`
  - `GET /api/notes/{id}`
  - `POST /api/notes`
  - `PUT /api/notes/{id}`
  - `DELETE /api/notes/{id}`
- Auth / Team Validation
  - `POST /api/auth/login`
  - `POST /api/auth/validate-team`

#### gRPC APIs (server still available)
- `auth.UserAuth`
  - `Login`
  - `CreateUser`
- `teamnotes.TeamNotes`
  - `ValidateTeam`
  - `StoreNote`
  - `GetStoredNotes`

### 2) NotesGrpcClient (`NotesGrpcClient/NotesGrpcClient.csproj`)
Console client flow (REST):
1. login using `/api/auth/login`
2. team verification using `/api/auth/validate-team`
3. optional note create using `/api/notes`
4. note list using `/api/notes`

## Validation Rules
- Login fails if user is not assigned to any team.
- Team validation fails if user is not in requested team.
- Team-scoped note integrity checks are enforced in service logic.

## Data Storage
Server-side JSON files (`NotesService/App_Data`):
- `users.json` - users and passwords
- `teams.json` - team membership
- `teamnotes.json` - persisted team notes

## Protobuf Contracts
Located in `NotesService/Protos`:
- `auth.proto`
- `teamnotes.proto`

## Run Instructions
1. Start `NotesService` with the **https** profile (`https://localhost:7232`).
2. Run `NotesGrpcClient`.
3. Enter username/password and team name.
4. Optionally create a note, then review notes returned by REST API.

## Default Seed Users
If `users.json` is missing or empty at startup:
- `admin / password123`
- `raj / notes@2026`
