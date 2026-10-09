using System.Collections.Concurrent;
using System.Text.Json;
using NotesService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

var app = builder.Build();

var usersFilePath = Path.Combine(app.Environment.ContentRootPath, "App_Data", "users.json");
var teamsFilePath = Path.Combine(app.Environment.ContentRootPath, "App_Data", "teams.json");

app.MapGrpcService<UserAuthService>();
app.MapGrpcService<TeamNotesService>();

var notes = new ConcurrentDictionary<Guid, Note>();

app.MapGet("/api/notes", () => Results.Ok(notes.Values.OrderBy(n => n.CreatedAtUtc)));

app.MapGet("/api/notes/{id:guid}", (Guid id) =>
{
    return notes.TryGetValue(id, out var note)
        ? Results.Ok(note)
        : Results.NotFound();
});

app.MapPost("/api/notes", (CreateNoteRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Title is required." });
    }

    var note = new Note(
        Guid.NewGuid(),
        request.Title.Trim(),
        request.Content?.Trim() ?? string.Empty,
        DateTime.UtcNow,
        DateTime.UtcNow);

    notes[note.Id] = note;
    return Results.Created($"/api/notes/{note.Id}", note);
});

app.MapPut("/api/notes/{id:guid}", (Guid id, UpdateNoteRequest request) =>
{
    if (!notes.TryGetValue(id, out var existing))
    {
        return Results.NotFound();
    }

    var updated = existing with
    {
        Title = string.IsNullOrWhiteSpace(request.Title) ? existing.Title : request.Title.Trim(),
        Content = request.Content is null ? existing.Content : request.Content.Trim(),
        UpdatedAtUtc = DateTime.UtcNow
    };

    notes[id] = updated;
    return Results.Ok(updated);
});

app.MapDelete("/api/notes/{id:guid}", (Guid id) =>
{
    return notes.TryRemove(id, out _)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapPost("/api/auth/login", (RestLoginRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Ok(new RestAuthReply(false, "Username and password are required."));
    }

    var users = LoadUsers(usersFilePath);
    var username = request.Username.Trim();
    var isValid = users.TryGetValue(username, out var password) && password == request.Password;
    if (!isValid)
    {
        return Results.Ok(new RestAuthReply(false, "Invalid username or password."));
    }

    var teams = LoadTeams(teamsFilePath);
    var inAnyTeam = teams.Values.Any(members => members.Contains(username, StringComparer.OrdinalIgnoreCase));
    if (!inAnyTeam)
    {
        return Results.Ok(new RestAuthReply(false, "Login failed: user is not assigned to any team."));
    }

    return Results.Ok(new RestAuthReply(true, "Login successful."));
});

app.MapPost("/api/auth/validate-team", (RestValidateTeamRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.TeamName))
    {
        return Results.Ok(new RestAuthReply(false, "Username and team name are required."));
    }

    var teams = LoadTeams(teamsFilePath);
    var teamName = request.TeamName.Trim();
    var username = request.Username.Trim();

    if (!teams.TryGetValue(teamName, out var members) || !members.Contains(username, StringComparer.OrdinalIgnoreCase))
    {
        return Results.Ok(new RestAuthReply(false, "User is not a member of the specified team."));
    }

    return Results.Ok(new RestAuthReply(true, "Team validation successful."));
});

app.MapGet("/", () => Results.Ok(new
{
    service = "NotesService",
    type = "REST + gRPC API",
    endpoints = new[]
    {
        "GET /api/notes",
        "GET /api/notes/{id}",
        "POST /api/notes",
        "PUT /api/notes/{id}",
        "DELETE /api/notes/{id}",
        "POST /api/auth/login",
        "POST /api/auth/validate-team",
        "gRPC: auth.UserAuth/Login",
        "gRPC: auth.UserAuth/CreateUser",
        "gRPC: teamnotes.TeamNotes/ValidateTeam",
        "gRPC: teamnotes.TeamNotes/StoreNote",
        "gRPC: teamnotes.TeamNotes/GetStoredNotes"
    }
}));

app.Run();

static Dictionary<string, string> LoadUsers(string usersFilePath)
{
    if (!File.Exists(usersFilePath))
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    var json = File.ReadAllText(usersFilePath);
    var users = JsonSerializer.Deserialize<List<UserCredential>>(json) ?? [];

    return users
        .Where(u => !string.IsNullOrWhiteSpace(u.Username))
        .ToDictionary(u => u.Username.Trim(), u => u.Password ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

static Dictionary<string, List<string>> LoadTeams(string teamsFilePath)
{
    if (!File.Exists(teamsFilePath))
    {
        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    var json = File.ReadAllText(teamsFilePath);
    var teams = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
    return teams ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
}

internal sealed record Note(Guid Id, string Title, string Content, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
internal sealed record RestLoginRequest(string Username, string Password);
internal sealed record RestValidateTeamRequest(string Username, string TeamName);
internal sealed record RestAuthReply(bool Success, string Message);
internal sealed record UserCredential(string Username, string Password);

internal sealed record CreateNoteRequest(string Title, string? Content);

internal sealed record UpdateNoteRequest(string? Title, string? Content);
