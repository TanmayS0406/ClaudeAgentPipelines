using System.Text.RegularExpressions;
using ClaudeAgentPipelines.Data;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAgentPipelines.Billing;

/// <summary>People who sign in to the app. Passwords are stored as salted PBKDF2 hashes, never as text.</summary>
public sealed partial class UserService(IDbContextFactory<AppDbContext> databases, IPasswordHasher<AppUser> hasher, ILogger<UserService> logger)
{
    public const int MinPasswordLength = 6;

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$")]
    private static partial Regex UserNamePattern();

    /// <summary>The user when the name and password are right and the account is active; otherwise null.</summary>
    public async Task<AppUser?> AuthenticateAsync(string? userName, string? password, CancellationToken cancellationToken = default)
    {
        userName = userName?.Trim();
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var user = string.IsNullOrEmpty(userName) ? null : await db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);

        // Hash even when there is no such user, so the answer takes as long either way.
        var hash = user?.PasswordHash ?? hasher.HashPassword(new AppUser(), "no such user");
        var result = hasher.VerifyHashedPassword(user ?? new AppUser(), hash, password ?? string.Empty);
        if (user is null || !user.IsActive || result == PasswordVerificationResult.Failed) return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password!);
            await db.SaveChangesAsync(cancellationToken);
        }
        return user;
    }

    public async Task<AppUser?> GetAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return null;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    /// <summary>True when the user exists and may still sign in. Checked on every request, so a disabled user is signed out.</summary>
    public async Task<bool> IsActiveAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (!Ids.IsValid(id)) return false;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        return await db.Users.AnyAsync(u => u.Id == id && u.IsActive, cancellationToken);
    }

    public async Task SaveContactAsync(string userId, string? email, string? phone, CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return;
        user.Email = string.IsNullOrWhiteSpace(email) ? user.Email : email.Trim();
        user.Phone = string.IsNullOrWhiteSpace(phone) ? user.Phone : phone.Trim();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Checks the details for a new user and builds it, or explains what is wrong. Does not save.</summary>
    public (AppUser? User, string? Error) Build(string? userName, string? displayName, string? password, string? email)
    {
        userName = userName?.Trim() ?? string.Empty;
        if (!UserNamePattern().IsMatch(userName))
        {
            return (null, "The user name must be 3 to 32 characters: letters, digits, dot, dash or underscore.");
        }
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return (null, $"The password must be at least {MinPasswordLength} characters.");
        }

        var user = new AppUser
        {
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim()
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        return (user, null);
    }

    /// <summary>First start: with no users at all, creates the user "test" with the password "test".</summary>
    public async Task EnsureSeedUserAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken)) return;

        var user = new AppUser { UserName = "test", DisplayName = "Test user" };
        user.PasswordHash = hasher.HashPassword(user, "test");
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Created the default user \"test\" (password \"test\"). Change it before sharing this app with anyone.");
    }
}
