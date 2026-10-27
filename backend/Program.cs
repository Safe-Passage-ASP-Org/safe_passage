using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SafePassage.Api.Data;
using SafePassage.Api.Hubs;
using SafePassage.Api.Middleware;
using SafePassage.Api.Models;
using SafePassage.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var databaseProvider = builder.Configuration["Database:Provider"] ?? "SqlServer";
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    switch (databaseProvider.Trim().ToLowerInvariant())
    {
        case "sqlserver":
            options.UseSqlServer(connectionString);
            break;
        case "postgresql":
        case "postgres":
            options.UseNpgsql(connectionString);
            break;
        case "sqlite":
            options.UseSqlite(ResolveSqliteConnectionString(connectionString, builder.Environment.ContentRootPath));
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported Database:Provider '{databaseProvider}'. Use 'SqlServer', 'PostgreSQL', or 'Sqlite'.");
    }
});

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
    ?? throw new InvalidOperationException("Jwt configuration is required.");
jwtSettings.Validate();

builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthFlowService, AuthFlowService>();
builder.Services.AddScoped<ISecurityPolicyEvaluator, SecurityPolicyEvaluator>();
builder.Services.AddScoped<INotificationService, MockNotificationService>();
builder.Services.AddScoped<IDeviceManagementService, DeviceManagementService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length == 0)
    throw new InvalidOperationException("Configure at least one Cors:AllowedOrigins value for browser clients.");

builder.Services.AddCors(options =>
{
    options.AddPolicy("KnownOrigins", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// 2. Add Controller and OpenAPI support
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/mfaHub"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// 3. Configure HTTP pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Apply schema migrations and seed the initial test user, device, and activities.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        dbContext.Database.Migrate();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        DbInitializer.Initialize(dbContext, passwordHasher);
    }
    catch (Exception ex)
    {
        logger.LogCritical(
            ex,
            "Database initialization failed for provider '{Provider}'. Authentication, device, and " +
            "activity requests cannot be served until this is resolved. Check ConnectionStrings:" +
            "DefaultConnection and Database:Provider.",
            databaseProvider);
        throw;
    }
}

// 4. Enable CORS Middleware (Must be before Authentication/Authorization)
app.UseCors("KnownOrigins");

app.UseAuthentication();
app.UseMiddleware<SecurityMiddleware>();
app.UseAuthorization();

// 5. Map real-time MFA notifications and controller routes
app.MapHub<MfaHub>("/mfaHub");
app.MapControllers();

// 6. Test Endpoint (to quickly check if API is running)
app.MapGet("/api/status", () => Results.Ok(new
{
    Status = "Online",
    System = "Safe Passage API",
    Timestamp = DateTime.UtcNow
}))
.WithName("GetSystemStatus");

app.Run();

// SQLite resolves a relative Data Source against the process working directory, which differs
// depending on how the API is launched (project folder, repo root, or a published output folder).
// Anchor it to the content root so every launch uses the same database file.
static string ResolveSqliteConnectionString(string connectionString, string contentRootPath)
{
    var sqliteBuilder = new SqliteConnectionStringBuilder(connectionString);
    if (!string.IsNullOrWhiteSpace(sqliteBuilder.DataSource) && !Path.IsPathRooted(sqliteBuilder.DataSource))
        sqliteBuilder.DataSource = Path.GetFullPath(Path.Combine(contentRootPath, sqliteBuilder.DataSource));

    // SQLite will not create missing directories for the database file, so make sure the
    // folder exists before the provider tries to open it.
    var databaseDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
    if (!string.IsNullOrEmpty(databaseDirectory))
        Directory.CreateDirectory(databaseDirectory);

    return sqliteBuilder.ConnectionString;
}
