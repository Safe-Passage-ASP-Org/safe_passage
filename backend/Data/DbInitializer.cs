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

    public static void Initialize(ApplicationDbContext dbContext, IPasswordHasher<User> passwordHasher)
    {
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
            device = new Device
            {
                Id = TestDeviceId,
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

        // 3. Ensure a pending demo MFA session exists so the mobile Pending Request works with real DB
        var hashedDemoToken = HashSessionToken("mock-req-001");
        var mfaSession = dbContext.MfaSessions.FirstOrDefault(m => m.SessionToken == hashedDemoToken);
        if (mfaSession is null)
        {
            mfaSession = new MfaSession
            {
                Id = PendingMfaSessionId,
                UserId = user.Id,
                DeviceId = device.Id,
                SessionToken = hashedDemoToken,
                Status = MfaSessionStatus.Pending,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(365), // Long-lived for demo stability
                CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.MfaSessions.Add(mfaSession);
            dbContext.SaveChanges();
        }
        else if (mfaSession.Status != MfaSessionStatus.Pending)
        {
            // Reset to pending if previously approved/denied so the demo always works
            mfaSession.Status = MfaSessionStatus.Pending;
            mfaSession.ExpiresAt = DateTimeOffset.UtcNow.AddDays(365);
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
