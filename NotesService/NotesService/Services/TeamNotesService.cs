using Grpc.Core;
using System.Collections.Concurrent;
using System.Text.Json;

namespace NotesService.Services
{
    public class TeamNotesService(ILogger<TeamNotesService> logger, IWebHostEnvironment environment) : TeamNotes.TeamNotesBase
    {
        private static readonly object SyncRoot = new();
        private static bool IsLoaded;
        private static readonly ConcurrentDictionary<string, ConcurrentBag<TeamNoteEntry>> NotesByTeam =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly string notesFilePath = Path.Combine(environment.ContentRootPath, "App_Data", "teamnotes.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public override Task<ValidateTeamReply> ValidateTeam(ValidateTeamRequest request, ServerCallContext context)
        {
            if (!UserAuthService.TryGetUsernameByToken(request.Token, out var username))
            {
                return Task.FromResult(new ValidateTeamReply
                {
                    Success = false,
                    Message = "Authentication failed. Invalid or missing token."
                });
            }

            if (string.IsNullOrWhiteSpace(request.TeamName))
            {
                return Task.FromResult(new ValidateTeamReply
                {
                    Success = false,
                    Message = "Team name is required.",
                    Owner = username
                });
            }

            var teamName = request.TeamName.Trim();
            if (!UserAuthService.IsUserInTeam(username, teamName))
            {
                return Task.FromResult(new ValidateTeamReply
                {
                    Success = false,
                    Message = "User is not a member of the specified team.",
                    Owner = username,
                    TeamName = teamName
                });
            }

            return Task.FromResult(new ValidateTeamReply
            {
                Success = true,
                Message = "Team validation successful.",
                Owner = username,
                TeamName = teamName
            });
        }

        public override Task<StoreTeamNoteReply> StoreNote(StoreTeamNoteRequest request, ServerCallContext context)
        {
            EnsureNotesLoaded();

            if (!UserAuthService.TryGetUsernameByToken(request.Token, out var username))
            {
                return Task.FromResult(new StoreTeamNoteReply
                {
                    Success = false,
                    Message = "Authentication failed. Invalid or missing token."
                });
            }

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Task.FromResult(new StoreTeamNoteReply
                {
                    Success = false,
                    Message = "Title is required."
                });
            }

            if (string.IsNullOrWhiteSpace(request.TeamName))
            {
                return Task.FromResult(new StoreTeamNoteReply
                {
                    Success = false,
                    Message = "Team name is required."
                });
            }

            var teamName = request.TeamName.Trim();
            if (!UserAuthService.IsUserInTeam(username, teamName))
            {
                return Task.FromResult(new StoreTeamNoteReply
                {
                    Success = false,
                    Message = "User is not a member of the specified team."
                });
            }

            var note = new TeamNoteEntry(
                Guid.NewGuid().ToString("N"),
                request.Title.Trim(),
                request.Content?.Trim() ?? string.Empty,
                DateTime.UtcNow,
                teamName,
                username);

            if (!IsNoteValidForTeam(note, teamName) || !string.Equals(note.Owner, username, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new StoreTeamNoteReply
                {
                    Success = false,
                    Message = "User and team do not match for this note."
                });
            }

            var teamNotes = NotesByTeam.GetOrAdd(teamName, _ => new ConcurrentBag<TeamNoteEntry>());
            teamNotes.Add(note);
            PersistNotes();

            logger.LogInformation("Team note stored for team {TeamName} by user {Username} with NoteId {NoteId}", teamName, username, note.NoteId);

            return Task.FromResult(new StoreTeamNoteReply
            {
                Success = true,
                Message = "Team note stored successfully.",
                NoteId = note.NoteId,
                Owner = username,
                CreatedAtUtc = note.CreatedAtUtc.ToString("O"),
                TeamName = teamName
            });
        }

        public override Task<GetStoredNotesReply> GetStoredNotes(GetStoredNotesRequest request, ServerCallContext context)
        {
            EnsureNotesLoaded();

            if (!UserAuthService.TryGetUsernameByToken(request.Token, out var username))
            {
                return Task.FromResult(new GetStoredNotesReply
                {
                    Success = false,
                    Message = "Authentication failed. Invalid or missing token."
                });
            }

            if (string.IsNullOrWhiteSpace(request.TeamName))
            {
                return Task.FromResult(new GetStoredNotesReply
                {
                    Success = false,
                    Message = "Team name is required."
                });
            }

            var teamName = request.TeamName.Trim();
            if (!UserAuthService.IsUserInTeam(username, teamName))
            {
                return Task.FromResult(new GetStoredNotesReply
                {
                    Success = false,
                    Message = "User is not a member of the specified team."
                });
            }

            var reply = new GetStoredNotesReply
            {
                Success = true,
                Message = "Notes retrieved successfully.",
                Owner = username,
                TeamName = teamName
            };

            if (NotesByTeam.TryGetValue(teamName, out var teamNotes))
            {
                foreach (var note in teamNotes.OrderBy(n => n.CreatedAtUtc))
                {
                    if (!IsNoteValidForTeam(note, teamName))
                    {
                        logger.LogWarning("Rejected note {NoteId} due to owner/team mismatch. Team={TeamName}, Owner={Owner}", note.NoteId, teamName, note.Owner);
                        return Task.FromResult(new GetStoredNotesReply
                        {
                            Success = false,
                            Message = "One or more notes were rejected because user and team do not match.",
                            Owner = username,
                            TeamName = teamName
                        });
                    }

                    reply.Notes.Add(new TeamNote
                    {
                        NoteId = note.NoteId,
                        Title = note.Title,
                        Content = note.Content,
                        CreatedAtUtc = note.CreatedAtUtc.ToString("O"),
                        TeamName = note.TeamName
                    });
                }
            }

            logger.LogInformation("Retrieved {Count} team notes for team {TeamName} requested by {Username}", reply.Notes.Count, teamName, username);
            return Task.FromResult(reply);
        }

        private static bool IsNoteValidForTeam(TeamNoteEntry note, string teamName)
        {
            if (!string.Equals(note.TeamName, teamName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return UserAuthService.IsUserInTeam(note.Owner, teamName);
        }

        private void EnsureNotesLoaded()
        {
            if (IsLoaded)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (IsLoaded)
                {
                    return;
                }

                try
                {
                    if (File.Exists(notesFilePath))
                    {
                        var json = File.ReadAllText(notesFilePath);
                        var saved = JsonSerializer.Deserialize<Dictionary<string, List<TeamNoteEntry>>>(json);

                        if (saved is not null)
                        {
                            foreach (var pair in saved)
                            {
                                NotesByTeam[pair.Key] = new ConcurrentBag<TeamNoteEntry>(pair.Value);
                            }
                        }
                    }

                    IsLoaded = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load team notes from JSON file {Path}", notesFilePath);
                    IsLoaded = true;
                }
            }
        }

        private void PersistNotes()
        {
            lock (SyncRoot)
            {
                try
                {
                    var directory = Path.GetDirectoryName(notesFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var snapshot = NotesByTeam.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.OrderBy(n => n.CreatedAtUtc).ToList(),
                        StringComparer.OrdinalIgnoreCase);

                    var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                    File.WriteAllText(notesFilePath, json);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to persist team notes to JSON file {Path}", notesFilePath);
                }
            }
        }

        private sealed record TeamNoteEntry(string NoteId, string Title, string Content, DateTime CreatedAtUtc, string TeamName, string Owner);
    }
}
