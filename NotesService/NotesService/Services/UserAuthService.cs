using Grpc.Core;
using System.Collections.Concurrent;
using System.Text.Json;

namespace NotesService.Services
{
    public class UserAuthService(ILogger<UserAuthService> logger, IWebHostEnvironment environment) : UserAuth.UserAuthBase
    {
        private static readonly object SyncRoot = new();
        private static bool IsLoaded;
        private static readonly ConcurrentDictionary<string, string> Users = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, HashSet<string>> Teams = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, string> Sessions = new(StringComparer.Ordinal);
        private readonly string usersFilePath = Path.Combine(environment.ContentRootPath, "App_Data", "users.json");
        private readonly string teamsFilePath = Path.Combine(environment.ContentRootPath, "App_Data", "teams.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static bool TryGetUsernameByToken(string token, out string username)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                username = string.Empty;
                return false;
            }

            return Sessions.TryGetValue(token.Trim(), out username!);
        }

        public static bool IsUserInTeam(string username, string teamName)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(teamName))
            {
                return false;
            }

            lock (SyncRoot)
            {
                return Teams.TryGetValue(teamName.Trim(), out var members)
                    && members.Contains(username.Trim());
            }
        }

        private static bool IsUserInAnyTeam(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return false;
            }

            lock (SyncRoot)
            {
                var normalizedUsername = username.Trim();
                return Teams.Values.Any(members => members.Contains(normalizedUsername));
            }
        }

        public override Task<LoginReply> Login(LoginRequest request, ServerCallContext context)
        {
            EnsureUsersLoaded();

            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Task.FromResult(new LoginReply
                {
                    Success = false,
                    Message = "Username and password are required."
                });
            }

            var isValid = Users.TryGetValue(request.Username.Trim(), out var password)
                && password == request.Password;

            if (!isValid)
            {
                logger.LogWarning("Invalid login attempt for user {Username}", request.Username);
                return Task.FromResult(new LoginReply
                {
                    Success = false,
                    Message = "Invalid username or password."
                });
            }

            if (!IsUserInAnyTeam(request.Username.Trim()))
            {
                logger.LogWarning("Login blocked for user {Username} because user is not assigned to any team", request.Username);
                return Task.FromResult(new LoginReply
                {
                    Success = false,
                    Message = "Login failed: user is not assigned to any team."
                });
            }

            var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            Sessions[token] = request.Username.Trim();
            logger.LogInformation("User {Username} logged in successfully", request.Username);

            return Task.FromResult(new LoginReply
            {
                Success = true,
                Message = "Login successful.",
                Token = token
            });
        }

        public override Task<CreateUserReply> CreateUser(CreateUserRequest request, ServerCallContext context)
        {
            EnsureUsersLoaded();

            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Task.FromResult(new CreateUserReply
                {
                    Success = false,
                    Message = "Username and password are required."
                });
            }

            var username = request.Username.Trim();

            lock (SyncRoot)
            {
                if (!Users.TryAdd(username, request.Password))
                {
                    return Task.FromResult(new CreateUserReply
                    {
                        Success = false,
                        Message = "User already exists."
                    });
                }

                AddUserToDefaultTeam(username);
                PersistUsers();
                PersistTeams();
            }

            logger.LogInformation("User {Username} created successfully", username);

            return Task.FromResult(new CreateUserReply
            {
                Success = true,
                Message = "User created successfully."
            });
        }

        private void EnsureUsersLoaded()
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
                    if (File.Exists(usersFilePath))
                    {
                        var json = File.ReadAllText(usersFilePath);
                        var saved = JsonSerializer.Deserialize<List<UserCredential>>(json);

                        if (saved is not null)
                        {
                            foreach (var user in saved)
                            {
                                if (!string.IsNullOrWhiteSpace(user.Username))
                                {
                                    Users[user.Username.Trim()] = user.Password ?? string.Empty;
                                }
                            }
                        }
                        else
                        {
                            var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                            if (legacy is not null)
                            {
                                foreach (var pair in legacy)
                                {
                                    Users[pair.Key] = pair.Value;
                                }
                            }
                        }
                    }

                    if (Users.IsEmpty)
                    {
                        Users["admin"] = "password123";
                        Users["raj"] = "notes@2026";
                        PersistUsers();
                    }

                    LoadTeams();
                    EnsureAllUsersAreInATeam();
                    PersistTeams();

                    IsLoaded = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load users from JSON file {Path}", usersFilePath);
                    IsLoaded = true;
                }
            }
        }

        private void PersistUsers()
        {
            lock (SyncRoot)
            {
                try
                {
                    var directory = Path.GetDirectoryName(usersFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var snapshot = Users
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => new UserCredential(pair.Key, pair.Value))
                        .ToList();

                    var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                    File.WriteAllText(usersFilePath, json);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to persist users to JSON file {Path}", usersFilePath);
                }
            }
        }

        private void LoadTeams()
        {
            Teams.Clear();

            if (File.Exists(teamsFilePath))
            {
                var json = File.ReadAllText(teamsFilePath);
                var saved = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);

                if (saved is not null)
                {
                    foreach (var pair in saved)
                    {
                        Teams[pair.Key] = new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }

            if (Teams.IsEmpty)
            {
                Teams["General"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void EnsureAllUsersAreInATeam()
        {
            var allMembers = Teams.Values.SelectMany(members => members).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var username in Users.Keys)
            {
                if (!allMembers.Contains(username))
                {
                    AddUserToDefaultTeam(username);
                }
            }
        }

        private void AddUserToDefaultTeam(string username)
        {
            var members = Teams.GetOrAdd("General", _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            members.Add(username);
        }

        private void PersistTeams()
        {
            lock (SyncRoot)
            {
                try
                {
                    var directory = Path.GetDirectoryName(teamsFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var snapshot = Teams.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.OrderBy(member => member).ToList(),
                        StringComparer.OrdinalIgnoreCase);

                    var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                    File.WriteAllText(teamsFilePath, json);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to persist teams to JSON file {Path}", teamsFilePath);
                }
            }
        }

        private sealed record UserCredential(string Username, string Password);
    }
}
