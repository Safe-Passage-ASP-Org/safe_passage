using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Contracts.Auth;
using SafePassage.Api.Data;
using SafePassage.Api.Models;

namespace SafePassage.Api.Services;

public enum AccountRegistrationResult
{
    Registered,
    AlreadyExists,
    UsernameUnavailable,
    EmailDeliveryFailed
}

public interface IAccountRegistrationService
{
    Task<AccountRegistrationResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
}

public sealed class AccountRegistrationService(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IAccountActivationService accountActivationService) : IAccountRegistrationService
{
    public async Task<AccountRegistrationResult> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var fullName = request.FullName.Trim();
        var username = request.Username.Trim().ToLowerInvariant();
        var email = request.Email.Trim().ToLowerInvariant();
        var emailLookup = email.ToUpperInvariant();
        var usernameLookup = username.ToUpperInvariant();
        var phoneNumber = request.PhoneNumber.Trim();

        if (await dbContext.Users.AnyAsync(user =>
                user.Email.ToUpper() == emailLookup || user.Username.ToUpper() == emailLookup,
                cancellationToken))
        {
            return AccountRegistrationResult.AlreadyExists;
        }

        if (await dbContext.Users.AnyAsync(user =>
                user.Username.ToUpper() == usernameLookup || user.Email.ToUpper() == usernameLookup,
                cancellationToken))
        {
            return AccountRegistrationResult.UsernameUnavailable;
        }

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = username,
            Email = email,
            FullName = fullName,
            PhoneNumber = phoneNumber,
            Role = "User",
            IsActive = false,
            IsEmailVerified = false
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        dbContext.Users.Add(user);
        dbContext.ActivityLogs.Add(new ActivityLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Timestamp = DateTimeOffset.UtcNow,
            Action = "User Registered",
            Resource = user.Email,
            Status = ActivityLogStatus.Success
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            return AccountRegistrationResult.AlreadyExists;
        }

        try
        {
            await accountActivationService.CreateAndSendInvitationAsync(
                user,
                cancellationToken,
                requiresPasswordSetup: false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch (EmailDeliveryException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            else
            {
                dbContext.AccountActivationTokens.RemoveRange(
                    dbContext.AccountActivationTokens.Local.Where(token => token.UserId == user.Id));
                dbContext.ActivityLogs.RemoveRange(
                    dbContext.ActivityLogs.Local.Where(activity => activity.UserId == user.Id
                        && activity.Action == "User Registered"));
                dbContext.Users.Remove(user);
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            return AccountRegistrationResult.EmailDeliveryFailed;
        }

        return AccountRegistrationResult.Registered;
    }
}
