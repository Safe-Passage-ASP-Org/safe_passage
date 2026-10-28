using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Models;

namespace SafePassage.Api.Data;

public static class DbInitializer
{
    public static readonly Guid TestUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TestDeviceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid PendingMfaSessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid MobileApplicationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid AdminApplicationId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static void Initialize(ApplicationDbContext dbContext, IPasswordHasher<User> passwordHasher)
    {
        EnsureApplication(dbContext, MobileApplicationId, "Safe Passage Mobile", "Safe Passage mobile client.");
        EnsureApplication(dbContext, AdminApplicationId, "Safe Passage Admin", "Safe Passage administration portal.");
        EnsurePolicy(
            dbContext,
            "Require MFA and trusted devices",
            "Authentication",
            "{\"requiresTrustedDevice\":true,\"allowTrustedDeviceMfaBypass\":false,\"requiresStepUp\":true,\"requireMfa\":true}");
        EnsurePolicy(
            dbContext,
            "Trusted device access",
            "Device",
            "{\"requiresTrustedDevice\":true,\"allowTrustedDeviceMfaBypass\":false,\"requiresStepUp\":true,\"requireMfa\":true}");

        // 1. Ensure test user exists
        var user = dbContext.Users.FirstOrDefault(u => u.Username == "john.doe");
        if (user is null)
        {
            user = new User
            {
                Id = TestUserId,
                Username = "john.doe",
                Email = "john.doe@enterprise.com",
                Role = "Admin",
                IsActive = true
            };
            user.PasswordHash = passwordHasher.HashPassword(user, "Password123!");
            dbContext.Users.Add(user);
            dbContext.SaveChanges();
        }

        // 2. Ensure test primary device exists for this user
        var device = dbContext.Devices.FirstOrDefault(d => d.DeviceId == "PRIMARY-ANDROID-001");
        if (device is null)
        {
            // A previous enrollment or database restore may already use the fixed test GUID
            // for a different device. Preserve that record and give this seed device a new ID.
            var seedDeviceId = dbContext.Devices.Any(d => d.Id == TestDeviceId)
                ? Guid.NewGuid()
                : TestDeviceId;

            device = new Device
            {
                Id = seedDeviceId,
                UserId = user.Id,
                DeviceId = "PRIMARY-ANDROID-001",
                DeviceName = "John's Primary Android",
                DeviceType = "Phone",
                OS = "Android 14",
                RegistrationDate = DateTimeOffset.UtcNow.AddDays(-14),
                LastActiveDate = DateTimeOffset.UtcNow,
                Status = DeviceStatus.Active,
                TrustStatus = DeviceTrustStatus.Trusted
            };
            dbContext.Devices.Add(device);
            dbContext.SaveChanges();
        }

        // 3. Create or update the demo MFA session with the correct Base64Url hash.
        var hashedDemoToken = HashSessionToken("mock-req-001");
        var mfaSession = dbContext.MfaSessions.FirstOrDefault(m => m.Id == PendingMfaSessionId)
            ?? dbContext.MfaSessions.FirstOrDefault(m => m.SessionToken == hashedDemoToken);
        if (mfaSession is null)
        {
            mfaSession = new MfaSession
            {
                Id = PendingMfaSessionId,
                UserId = user.Id,
                DeviceId = device.Id,
                SessionToken = hashedDemoToken,
                Status = MfaSessionStatus.Pending,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.MfaSessions.Add(mfaSession);
            dbContext.SaveChanges();
        }
        else
        {
            mfaSession.SessionToken = hashedDemoToken;
            if (mfaSession.Status == MfaSessionStatus.Pending && mfaSession.ExpiresAt < DateTimeOffset.UtcNow)
            {
                mfaSession.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
            }
            dbContext.SaveChanges();
        }

        // 4. Ensure initial activity logs exist so Activity Tab displays live history
        if (!dbContext.ActivityLogs.Any(a => a.UserId == user.Id))
        {
            dbContext.ActivityLogs.AddRange(
                new ActivityLog
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Action = "Successful Login",
                    Resource = "Web Portal",
                    Status = ActivityLogStatus.Success,
                    Timestamp = DateTimeOffset.UtcNow.AddMinutes(-25)
                },
                new ActivityLog
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Action = "App Access Granted",
                    Resource = "Salesforce Enterprise",
                    Status = ActivityLogStatus.Success,
                    Timestamp = DateTimeOffset.UtcNow.AddHours(-2)
                },
                new ActivityLog
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Action = "Device Registered",
                    Resource = "John's Primary Android",
                    Status = ActivityLogStatus.Success,
                    Timestamp = DateTimeOffset.UtcNow.AddDays(-1)
                }
            );
            dbContext.SaveChanges();
        }
    }

    private static string HashSessionToken(string token)
    {
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static void EnsureApplication(ApplicationDbContext dbContext, Guid id, string name, string description)
    {
        if (dbContext.Applications.Any(application => application.Id == id || application.Name == name))
            return;

        dbContext.Applications.Add(new Application { Id = id, Name = name, Description = description });
        dbContext.SaveChanges();
    }

    private static void EnsurePolicy(ApplicationDbContext dbContext, string name, string type, string rulesJson)
    {
        if (dbContext.SecurityPolicies.Any(policy => policy.PolicyName == name))
            return;

        dbContext.SecurityPolicies.Add(new SecurityPolicy
        {
            Id = Guid.NewGuid(),
            PolicyName = name,
            PolicyType = type,
            IsEnforced = true,
            RulesJson = rulesJson
        });
        dbContext.SaveChanges();
    }
}
