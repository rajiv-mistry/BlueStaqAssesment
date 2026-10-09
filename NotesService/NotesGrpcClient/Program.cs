using System.Net.Http.Json;

var serviceUrl = "https://localhost:7232";
using var httpClient = new HttpClient { BaseAddress = new Uri(serviceUrl) };

Console.WriteLine($"Notes REST client connected to {serviceUrl}");
Console.Write("Username: ");
var username = Console.ReadLine()?.Trim() ?? string.Empty;
Console.Write("Password: ");
var password = Console.ReadLine() ?? string.Empty;

if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
{
    Console.WriteLine("Username and password are required.");
    return;
}

var loginResponse = await httpClient.PostAsJsonAsync("/api/auth/login", new RestLoginRequest(username, password));
var loginResult = await loginResponse.Content.ReadFromJsonAsync<RestAuthReply>();

if (loginResult is null || !loginResult.Success)
{
    Console.WriteLine($"Login failed: {loginResult?.Message ?? "Unexpected error"}");
    return;
}

Console.WriteLine("Login successful.");
Console.Write("Team name: ");
var teamName = Console.ReadLine()?.Trim() ?? string.Empty;

if (string.IsNullOrWhiteSpace(teamName))
{
    Console.WriteLine("Team name is required.");
    return;
}

var validateResponse = await httpClient.PostAsJsonAsync("/api/auth/validate-team", new RestValidateTeamRequest(username, teamName));
var validateResult = await validateResponse.Content.ReadFromJsonAsync<RestAuthReply>();

if (validateResult is null || !validateResult.Success)
{
    Console.WriteLine($"Team validation failed: {validateResult?.Message ?? "Unexpected error"}");
    return;
}

Console.WriteLine("Team validation successful.");

Console.Write("Note title (leave empty to skip create): ");
var title = Console.ReadLine()?.Trim() ?? string.Empty;

if (!string.IsNullOrWhiteSpace(title))
{
    Console.Write("Note content: ");
    var content = Console.ReadLine() ?? string.Empty;

    var createResponse = await httpClient.PostAsJsonAsync("/api/notes", new CreateNoteRequest(title, content));
    if (!createResponse.IsSuccessStatusCode)
    {
        Console.WriteLine($"Create failed: {(int)createResponse.StatusCode} {createResponse.ReasonPhrase}");
        return;
    }

    var createdNote = await createResponse.Content.ReadFromJsonAsync<NoteDto>();
    Console.WriteLine(createdNote is null
        ? "Note created."
        : $"Note created with id: {createdNote.Id}");
}

var notes = await httpClient.GetFromJsonAsync<List<NoteDto>>("/api/notes");
if (notes is null)
{
    Console.WriteLine("No notes returned from API.");
    return;
}

Console.WriteLine("\nNotes:");
foreach (var note in notes)
{
    Console.WriteLine($"- [{note.CreatedAtUtc:O}] {note.Title}: {note.Content}");
}

internal sealed record CreateNoteRequest(string Title, string? Content);
internal sealed record RestLoginRequest(string Username, string Password);
internal sealed record RestValidateTeamRequest(string Username, string TeamName);
internal sealed record RestAuthReply(bool Success, string Message);

internal sealed record NoteDto(Guid Id, string Title, string Content, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
