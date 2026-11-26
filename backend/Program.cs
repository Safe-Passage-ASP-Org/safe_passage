using System.Text;
using System.Text.Json.Serialization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
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
    options.ConfigureSafePassageDatabase(databaseProvider, connectionString, builder.Environment.ContentRootPath);
    options.UseOpenIddict();
    options.ConfigureWarnings(w =>
        w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
    ?? throw new InvalidOperationException("Jwt configuration is required.");
jwtSettings.Validate();

builder.Services.AddSingleton(jwtSettings);
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthFlowService, AuthFlowService>();
builder.Services.AddScoped<ISecurityPolicyEvaluator, SecurityPolicyEvaluator>();
builder.Services.AddScoped<IApplicationAccessVerificationService, ApplicationAccessVerificationService>();
builder.Services.AddScoped<INotificationService, MockNotificationService>();
builder.Services.AddScoped<IDeviceManagementService, DeviceManagementService>();
builder.Services.AddScoped<PendingDeviceRegistrationService>();
builder.Services.AddHttpClient<IActivationEmailSender, HttpApiActivationEmailSender>();
builder.Services.AddHttpClient<ISubscriptionExpirationEmailSender, HttpApiActivationEmailSender>();
builder.Services.AddHostedService<SubscriptionExpirationReminderWorker>();
builder.Services.AddScoped<IAccountActivationService, AccountActivationService>();
builder.Services.AddScoped<IAdminEmailLoginService, AdminEmailLoginService>();
builder.Services.AddSingleton<ApplicationCredentialProtector>();
builder.Services.AddScoped<IAccountRegistrationService, AccountRegistrationService>();
builder.Services.AddScoped<DeviceBootstrapService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<AccountActivationOptions>(builder.Configuration.GetSection(AccountActivationOptions.SectionName));
builder.Services.Configure<AdminLoginOptions>(builder.Configuration.GetSection(AdminLoginOptions.SectionName));
builder.Services.Configure<PayPalOptions>(builder.Configuration.GetSection(PayPalOptions.SectionName));
builder.Services.Configure<PayMongoOptions>(builder.Configuration.GetSection(PayMongoOptions.SectionName));
builder.Services.Configure<SubscriptionExpirationReminderOptions>(
    builder.Configuration.GetSection(SubscriptionExpirationReminderOptions.SectionName));
builder.Services.AddHttpClient<IPayPalClient, PayPalHttpClient>();
builder.Services.AddHttpClient<IPayMongoClient, PayMongoHttpClient>();
builder.Services.AddSingleton<DemoCheckoutStateStore>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();

var oidcIssuer = builder.Configuration["OpenIddict:Issuer"] ?? "http://localhost:5143";
builder.Services.AddOpenIddict()
    .AddCore(options => options
        .UseEntityFrameworkCore()
        .UseDbContext<ApplicationDbContext>())
    .AddServer(options =>
    {
        options.SetIssuer(new Uri(oidcIssuer))
            .SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token")
            .AllowAuthorizationCodeFlow()
            .RequireProofKeyForCodeExchange()
            .RegisterScopes(
                OpenIddictConstants.Scopes.Email,
                OpenIddictConstants.Scopes.Profile,
                OpenIddictConstants.Scopes.Roles)
            .SetUserInfoEndpointUris("/connect/userinfo")
            .UseAspNetCore(aspNetCoreOptions =>
            {
                aspNetCoreOptions.EnableAuthorizationEndpointPassthrough();
                aspNetCoreOptions.EnableUserInfoEndpointPassthrough();
                aspNetCoreOptions.EnableStatusCodePagesIntegration();
                if (builder.Environment.IsDevelopment())
                    aspNetCoreOptions.DisableTransportSecurityRequirement();
            });
        if (builder.Environment.IsDevelopment())
        {
            options.AddDevelopmentEncryptionCertificate()
                .AddDevelopmentSigningCertificate();
        }
        else
        {
            var signingCertificate = LoadCertificate(
                builder.Configuration["OpenIddict:SigningCertificatePath"],
                builder.Configuration["OpenIddict:SigningCertificateBase64"],
                builder.Configuration["OpenIddict:SigningCertificatePassword"]);
            var encryptionCertificate = LoadCertificate(
                builder.Configuration["OpenIddict:EncryptionCertificatePath"],
                builder.Configuration["OpenIddict:EncryptionCertificateBase64"],
                builder.Configuration["OpenIddict:EncryptionCertificatePassword"]);

            if (signingCertificate is not null && encryptionCertificate is not null)
            {
                if (!signingCertificate.HasPrivateKey || !encryptionCertificate.HasPrivateKey)
                    throw new InvalidOperationException("OpenIddict certificates must include their private keys.");
                options.AddSigningCertificate(signingCertificate)
                    .AddEncryptionCertificate(encryptionCertificate);
            }
            else
            {
                // Ephemeral keys allow stateless/PaaS deployments (e.g. Render) to boot without PFX files on disk.
                options.AddEphemeralEncryptionKey()
                    .AddEphemeralSigningKey();
            }
        }
    });

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

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
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
            },
            OnTokenValidated = async context =>
            {
                var userClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? context.Principal?.FindFirstValue("sub");
                if (!Guid.TryParse(userClaim, out var userId))
                {
                    context.Fail("Invalid subject claim.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                var isActive = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => (bool?)u.IsActive)
                    .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
                if (isActive != true)
                {
                    context.Fail("Account is inactive or suspended.");
                    return;
                }

                var jti = context.Principal?.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
                if (!string.IsNullOrWhiteSpace(jti))
                {
                    var isRevoked = await db.UserSessions.AsNoTracking()
                        .AnyAsync(s => s.TokenJti == jti && s.RevokedAt != null, context.HttpContext.RequestAborted);
                    if (isRevoked)
                    {
                        context.Fail("Session has been revoked.");
                        return;
                    }
                }

                var deviceId = context.Principal?.FindFirstValue("device_id");
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    var deviceStatus = await db.Devices.AsNoTracking()
                        .Where(d => d.UserId == userId && d.DeviceId == deviceId)
                        .Select(d => (DeviceStatus?)d.Status)
                        .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
                    if (deviceStatus is not null && deviceStatus != DeviceStatus.Active)
                    {
                        context.Fail("Device has been disabled.");
                        return;
                    }
                }
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
    })
    .AddCookie("oidc", options =>
    {
        options.Cookie.Name = "SafePassage.OidcSession";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddCookie("facebook-external", options =>
    {
        options.Cookie.Name = "SafePassage.Facebook.External";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        options.SlidingExpiration = false;
    })
    .AddCookie("github-external", options =>
    {
        options.Cookie.Name = "SafePassage.GitHub.External";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        options.SlidingExpiration = false;
    })
    .AddCookie("gitlab-external", options =>
    {
        options.Cookie.Name = "SafePassage.GitLab.External";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        options.SlidingExpiration = false;
    });

// Only register each OAuth provider when both ClientId and ClientSecret are configured.
// Registering with an empty ClientId causes ArgumentException on startup.
var facebookAppId = builder.Configuration["Facebook:AppId"];
var facebookAppSecret = builder.Configuration["Facebook:AppSecret"];
if (!string.IsNullOrWhiteSpace(facebookAppId) && !string.IsNullOrWhiteSpace(facebookAppSecret))
{
    builder.Services.AddAuthentication().AddOAuth("Facebook", options =>
    {
        var graphVersion = builder.Configuration["Facebook:GraphApiVersion"] ?? "v21.0";
        options.ClientId = facebookAppId;
        options.ClientSecret = facebookAppSecret;
        options.SignInScheme = "facebook-external";
        options.CallbackPath = "/signin-facebook";
        options.CorrelationCookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        // Facebook returns to this callback as a top-level GET. Lax works for that flow,
        // including local HTTP where browsers reject SameSite=None cookies without Secure.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.AuthorizationEndpoint = $"https://www.facebook.com/{graphVersion}/dialog/oauth?auth_type=rerequest";
        options.TokenEndpoint = $"https://graph.facebook.com/{graphVersion}/oauth/access_token";
        options.UserInformationEndpoint = $"https://graph.facebook.com/{graphVersion}/me?fields=id,name,email";
        options.SaveTokens = false;
        options.Scope.Add("public_profile");
        options.Scope.Add("email");
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
        options.Events.OnCreatingTicket = async context =>
        {
            var endpoint = $"{context.Options.UserInformationEndpoint}&access_token={Uri.EscapeDataString(context.AccessToken ?? string.Empty)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
            request.Headers.UserAgent.ParseAdd("SafePassage");
            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();
            var rawJson = await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted);
            Console.WriteLine($"[Facebook OAuth] Response from /me: {rawJson}");
            using var payload = System.Text.Json.JsonDocument.Parse(rawJson);
            context.RunClaimActions(payload.RootElement);

            if (payload.RootElement.TryGetProperty("email", out var emailProp) && !string.IsNullOrWhiteSpace(emailProp.GetString()))
            {
                var emailVal = emailProp.GetString()!;
                if (!context.Identity!.HasClaim(c => c.Type == ClaimTypes.Email))
                {
                    context.Identity.AddClaim(new Claim(ClaimTypes.Email, emailVal));
                }
            }
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("FacebookLogin")
                .LogWarning(context.Failure, "Facebook OAuth callback failed.");
            var frontend = builder.Configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092";
            var wasCancelled = string.Equals(context.Request.Query["error"], "access_denied", StringComparison.OrdinalIgnoreCase);
            var path = "/login?facebookError=" + (wasCancelled ? "cancelled" : "provider_error");
            if (context.Properties?.Items.TryGetValue("SafePassage:OidcReturnUrl", out var returnUrl) == true
                && !string.IsNullOrWhiteSpace(returnUrl))
                path += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
            context.Response.Redirect(new Uri(new Uri(frontend), path).ToString());
            return Task.CompletedTask;
        };
    });
}

var githubClientId = builder.Configuration["GitHub:ClientId"];
var githubClientSecret = builder.Configuration["GitHub:ClientSecret"];
if (!string.IsNullOrWhiteSpace(githubClientId) && !string.IsNullOrWhiteSpace(githubClientSecret))
{
    builder.Services.AddAuthentication().AddOAuth("GitHub", options =>
    {
        options.ClientId = githubClientId;
        options.ClientSecret = githubClientSecret;
        options.SignInScheme = "github-external";
        options.CallbackPath = "/signin-github";
        options.CorrelationCookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";
        options.SaveTokens = false;
        options.Scope.Add("read:user");
        options.Scope.Add("user:email");
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        options.Events.OnCreatingTicket = async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("SafePassage");
            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();
            using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
            context.RunClaimActions(payload.RootElement);

            using var emailRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
            emailRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
            emailRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
            emailRequest.Headers.UserAgent.ParseAdd("SafePassage");
            using var emailResponse = await context.Backchannel.SendAsync(emailRequest, context.HttpContext.RequestAborted);
            emailResponse.EnsureSuccessStatusCode();
            using var emailPayload = System.Text.Json.JsonDocument.Parse(await emailResponse.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
            var verifiedPrimaryEmail = emailPayload.RootElement.EnumerateArray()
                .Where(item => item.TryGetProperty("primary", out var primary) && primary.GetBoolean()
                    && item.TryGetProperty("verified", out var verified) && verified.GetBoolean())
                .Select(item => item.TryGetProperty("email", out var value) ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(verifiedPrimaryEmail))
                throw new InvalidOperationException("GitHub did not return a verified primary email address.");
            context.Identity!.AddClaim(new Claim(ClaimTypes.Email, verifiedPrimaryEmail));
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GitHubLogin")
                .LogWarning(context.Failure, "GitHub OAuth callback failed.");
            var frontend = builder.Configuration["GitHub:FrontendUrl"]
                ?? builder.Configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092";
            var wasCancelled = string.Equals(context.Request.Query["error"], "access_denied", StringComparison.OrdinalIgnoreCase);
            var missingVerifiedEmail = context.Failure?.Message.Contains("verified primary email", StringComparison.OrdinalIgnoreCase) == true;
            var errorCode = wasCancelled ? "cancelled" : missingVerifiedEmail ? "email_required" : "provider_error";
            var path = "/login?githubError=" + errorCode;
            if (context.Properties?.Items.TryGetValue("SafePassage:OidcReturnUrl", out var returnUrl) == true
                && !string.IsNullOrWhiteSpace(returnUrl))
                path += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
            context.Response.Redirect(new Uri(new Uri(frontend), path).ToString());
            return Task.CompletedTask;
        };
    });
}

var gitlabClientId = builder.Configuration["GitLab:ClientId"];
var gitlabClientSecret = builder.Configuration["GitLab:ClientSecret"];
if (!string.IsNullOrWhiteSpace(gitlabClientId) && !string.IsNullOrWhiteSpace(gitlabClientSecret))
{
    builder.Services.AddAuthentication().AddOAuth("GitLab", options =>
    {
        var gitLabInstance = builder.Configuration["GitLab:InstanceUrl"]?.TrimEnd('/') ?? "https://gitlab.com";
        options.ClientId = gitlabClientId;
        options.ClientSecret = gitlabClientSecret;
        options.SignInScheme = "gitlab-external";
        options.CallbackPath = "/signin-gitlab";
        options.CorrelationCookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.AuthorizationEndpoint = $"{gitLabInstance}/oauth/authorize";
        options.TokenEndpoint = $"{gitLabInstance}/oauth/token";
        options.UserInformationEndpoint = $"{gitLabInstance}/api/v4/user";
        options.SaveTokens = false;
        options.Scope.Add("read_user");
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
        options.Events.OnCreatingTicket = async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("SafePassage");
            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();
            using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
            context.RunClaimActions(payload.RootElement);

            if (!payload.RootElement.TryGetProperty("email", out var emailProp) || string.IsNullOrWhiteSpace(emailProp.GetString()))
            {
                // Try fetching from /api/v4/user/emails if primary email isn't directly exposed
                using var emailRequest = new HttpRequestMessage(HttpMethod.Get, $"{gitLabInstance}/api/v4/user/emails");
                emailRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
                emailRequest.Headers.Accept.ParseAdd("application/json");
                emailRequest.Headers.UserAgent.ParseAdd("SafePassage");
                using var emailResponse = await context.Backchannel.SendAsync(emailRequest, context.HttpContext.RequestAborted);
                if (emailResponse.IsSuccessStatusCode)
                {
                    using var emailPayload = System.Text.Json.JsonDocument.Parse(await emailResponse.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                    var firstEmail = emailPayload.RootElement.EnumerateArray()
                        .Select(item => item.TryGetProperty("email", out var val) ? val.GetString() : null)
                        .FirstOrDefault(val => !string.IsNullOrWhiteSpace(val));
                    if (!string.IsNullOrWhiteSpace(firstEmail))
                    {
                        context.Identity!.AddClaim(new Claim(ClaimTypes.Email, firstEmail));
                    }
                }
            }
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("GitLabLogin")
                .LogWarning(context.Failure, "GitLab OAuth callback failed.");
            var frontend = builder.Configuration["GitLab:FrontendUrl"]
                ?? builder.Configuration["GitHub:FrontendUrl"]
                ?? builder.Configuration["Facebook:FrontendUrl"] ?? "http://localhost:5092";
            var wasCancelled = string.Equals(context.Request.Query["error"], "access_denied", StringComparison.OrdinalIgnoreCase);
            var missingEmail = context.Failure?.Message.Contains("email", StringComparison.OrdinalIgnoreCase) == true;
            var errorCode = wasCancelled ? "cancelled" : missingEmail ? "email_required" : "provider_error";
            var path = "/login?gitlabError=" + errorCode;
            if (context.Properties?.Items.TryGetValue("SafePassage:OidcReturnUrl", out var returnUrl) == true
                && !string.IsNullOrWhiteSpace(returnUrl))
                path += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
            context.Response.Redirect(new Uri(new Uri(frontend), path).ToString());
            return Task.CompletedTask;
        };
    });
}

// UserInfo and authorization accept the OpenIddict access token scheme alongside the app's JWT scheme.
builder.Services.AddOpenIddict().AddValidation(options =>
{
    options.UseLocalServer();
    options.UseAspNetCore();
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireRole("Admin", "Administrator", "Super Admin"));
    options.AddPolicy("OperatorOrAdmin", policy =>
        policy.RequireRole("Admin", "Administrator", "Super Admin", "Operator"));
    options.AddPolicy("AuditorOrAbove", policy =>
        policy.RequireRole("Admin", "Administrator", "Super Admin", "Operator", "Auditor"));
});

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

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
        if (databaseProvider.Trim().ToLowerInvariant() is "postgresql" or "postgres")
        {
            // Supabase databases already contain system schemas/tables, so EnsureCreated() skips creation.
            // Check explicitly for our public."Applications" table and call CreateTables() if missing.
            var conn = dbContext.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                conn.Open();
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Applications');";
            var exists = checkCmd.ExecuteScalar() is true;
            if (!exists)
            {
                var creator = dbContext.GetService<IRelationalDatabaseCreator>();
                creator.CreateTables();
            }

            using var postureCmd = conn.CreateCommand();
            postureCmd.CommandText = """
                ALTER TABLE "Devices"
                  ADD COLUMN IF NOT EXISTS "ScreenLockEnabled" boolean NULL,
                  ADD COLUMN IF NOT EXISTS "IsRootedOrJailbroken" boolean NULL,
                  ADD COLUMN IF NOT EXISTS "PostureUpdatedAt" timestamp with time zone NULL,
                  ADD COLUMN IF NOT EXISTS "PushPlatform" text NULL,
                  ADD COLUMN IF NOT EXISTS "PushToken" text NULL;
                """;
            postureCmd.ExecuteNonQuery();

            using var adminLoginTokenCmd = conn.CreateCommand();
            adminLoginTokenCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS "AdminEmailChangeTokens" (
                    "Id" uuid PRIMARY KEY,
                    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    "NewEmail" character varying(320) NOT NULL,
                    "TokenHash" character varying(64) NOT NULL,
                    "OtpHash" character varying(64) NOT NULL,
                    "FailedAttempts" integer NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "ExpiresAt" timestamp with time zone NOT NULL,
                    "UsedAt" timestamp with time zone NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AdminEmailChangeTokens_TokenHash"
                    ON "AdminEmailChangeTokens" ("TokenHash");
                CREATE INDEX IF NOT EXISTS "IX_AdminEmailChangeTokens_UserId_ExpiresAt"
                    ON "AdminEmailChangeTokens" ("UserId", "ExpiresAt");
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "BackupEmail" character varying(320) NULL;
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "ProfileImageData" character varying(1400000) NULL;
                CREATE TABLE IF NOT EXISTS "UserSubscriptions" (
                    "Id" uuid PRIMARY KEY,
                    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    "PayPalSubscriptionId" character varying(128) NOT NULL,
                    "PayPalPlanId" character varying(128) NOT NULL,
                    "PlanCode" character varying(64) NOT NULL,
                    "Status" character varying(32) NOT NULL,
                    "ApprovalUrl" character varying(2048) NULL,
                    "CurrentPeriodStart" timestamp with time zone NULL,
                    "CurrentPeriodEnd" timestamp with time zone NULL,
                    "CancelledAt" timestamp with time zone NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserSubscriptions_PayPalSubscriptionId"
                    ON "UserSubscriptions" ("PayPalSubscriptionId");
                CREATE INDEX IF NOT EXISTS "IX_UserSubscriptions_UserId_Status"
                    ON "UserSubscriptions" ("UserId", "Status");
                ALTER TABLE "UserSubscriptions" ADD COLUMN IF NOT EXISTS "ExpirationReminderPeriodEnd" timestamp with time zone NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_BackupEmail"
                    ON "Users" ("BackupEmail") WHERE "BackupEmail" IS NOT NULL;
                CREATE TABLE IF NOT EXISTS "BackupEmailVerificationTokens" (
                    "Id" uuid PRIMARY KEY,
                    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    "NewEmail" character varying(320) NOT NULL,
                    "TokenHash" character varying(64) NOT NULL,
                    "OtpHash" character varying(64) NOT NULL,
                    "FailedAttempts" integer NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "ExpiresAt" timestamp with time zone NOT NULL,
                    "UsedAt" timestamp with time zone NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_BackupEmailVerificationTokens_TokenHash"
                    ON "BackupEmailVerificationTokens" ("TokenHash");
                CREATE INDEX IF NOT EXISTS "IX_BackupEmailVerificationTokens_UserId_ExpiresAt"
                    ON "BackupEmailVerificationTokens" ("UserId", "ExpiresAt");
                CREATE TABLE IF NOT EXISTS "AdminLoginTokens" (
                    "Id" uuid PRIMARY KEY,
                    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    "TokenHash" character varying(64) NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "ExpiresAt" timestamp with time zone NOT NULL,
                    "UsedAt" timestamp with time zone NULL,
                    "FailedAttempts" integer NOT NULL DEFAULT 0
                );
                ALTER TABLE "AdminLoginTokens" ADD COLUMN IF NOT EXISTS "FailedAttempts" integer NOT NULL DEFAULT 0;
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AdminLoginTokens_TokenHash"
                    ON "AdminLoginTokens" ("TokenHash");
                CREATE INDEX IF NOT EXISTS "IX_AdminLoginTokens_UserId_ExpiresAt"
                    ON "AdminLoginTokens" ("UserId", "ExpiresAt");
                ALTER TABLE "Applications"
                    ADD COLUMN IF NOT EXISTS "Category" character varying(64) NOT NULL DEFAULT 'Other',
                    ADD COLUMN IF NOT EXISTS "AutoFillUsername" character varying(320) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "EncryptedAutoFillPassword" character varying(2048) NOT NULL DEFAULT '';
                ALTER TABLE "Applications"
                    ADD COLUMN IF NOT EXISTS "IsPublished" boolean NOT NULL DEFAULT false,
                    ADD COLUMN IF NOT EXISTS "AllowAccessRequests" boolean NOT NULL DEFAULT false;
                CREATE TABLE IF NOT EXISTS "ApplicationAccessRequests" (
                    "Id" uuid PRIMARY KEY,
                    "ApplicationId" uuid NOT NULL REFERENCES "Applications" ("Id") ON DELETE CASCADE,
                    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    "Status" character varying(32) NOT NULL,
                    "Reason" character varying(1000) NOT NULL,
                    "RequestedAt" timestamp with time zone NOT NULL,
                    "ReviewedByUserId" uuid NULL,
                    "ReviewedAt" timestamp with time zone NULL,
                    "DecisionNote" character varying(1000) NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApplicationAccessRequests_ApplicationId_UserId"
                    ON "ApplicationAccessRequests" ("ApplicationId", "UserId") WHERE "Status" = 'Pending';
                CREATE INDEX IF NOT EXISTS "IX_ApplicationAccessRequests_UserId_RequestedAt"
                    ON "ApplicationAccessRequests" ("UserId", "RequestedAt");
                ALTER TABLE "MfaSessions"
                    ADD COLUMN IF NOT EXISTS "Purpose" character varying(64) NOT NULL DEFAULT 'Login';
                CREATE TABLE IF NOT EXISTS "VaultSites" (
                    "Id" uuid PRIMARY KEY,
                    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    "Name" character varying(200) NOT NULL,
                    "LoginUrl" character varying(2048) NOT NULL,
                    "Origin" character varying(512) NOT NULL,
                    "Category" character varying(64) NOT NULL,
                    "Description" character varying(2000) NOT NULL,
                    "AutoFillUsername" character varying(320) NOT NULL,
                    "EncryptedPassword" character varying(2048) NOT NULL,
                    "EncryptedTotpSecret" character varying(2048) NOT NULL,
                    "VerificationStatus" character varying(32) NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "VerifiedAt" timestamp with time zone NULL,
                    "VerifiedByUserId" uuid NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_VaultSites_UserId_Origin"
                    ON "VaultSites" ("UserId", "Origin");
                CREATE INDEX IF NOT EXISTS "IX_VaultSites_VerificationStatus_CreatedAt"
                    ON "VaultSites" ("VerificationStatus", "CreatedAt");
                ALTER TABLE "ActivityLogs"
                    ADD COLUMN IF NOT EXISTS "ActorUserId" uuid NULL,
                    ADD COLUMN IF NOT EXISTS "TargetUserId" uuid NULL,
                    ADD COLUMN IF NOT EXISTS "DeviceId" character varying(256) NULL,
                    ADD COLUMN IF NOT EXISTS "Reason" character varying(512) NULL,
                    ADD COLUMN IF NOT EXISTS "IpAddress" character varying(64) NULL;
                CREATE INDEX IF NOT EXISTS "IX_ActivityLogs_TargetUserId_Timestamp"
                    ON "ActivityLogs" ("TargetUserId", "Timestamp");
                CREATE TABLE IF NOT EXISTS "UserSessions" (
                    "Id" uuid PRIMARY KEY,
                    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                    "DeviceId" character varying(256) NULL,
                    "TokenJti" character varying(64) NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "ExpiresAt" timestamp with time zone NOT NULL,
                    "RevokedAt" timestamp with time zone NULL,
                    "RevokedReason" character varying(128) NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserSessions_TokenJti"
                    ON "UserSessions" ("TokenJti");
                CREATE INDEX IF NOT EXISTS "IX_UserSessions_UserId_DeviceId_RevokedAt"
                    ON "UserSessions" ("UserId", "DeviceId", "RevokedAt");
                """;
            adminLoginTokenCmd.ExecuteNonQuery();
        }
        else
        {
            dbContext.Database.Migrate();
        }
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var frontendBaseUrl = app.Configuration["Frontend:BaseUrl"]
            ?? app.Configuration["Facebook:FrontendUrl"]
            ?? app.Configuration["GitHub:FrontendUrl"]
            ?? (app.Environment.IsProduction()
                ? "https://safe-passage-web.onrender.com"
                : "http://localhost:5092");
        var financeDemoLaunchUrl = app.Configuration["FinanceDemo:LaunchUrl"]
            ?? new Uri(new Uri(frontendBaseUrl.TrimEnd('/') + "/"), "finance-demo").ToString();
        var seedDemoData = app.Environment.IsDevelopment()
            || app.Configuration.GetValue<bool>("Database:SeedDemoData");
        if (seedDemoData)
        {
            DbInitializer.Initialize(dbContext, passwordHasher, financeDemoLaunchUrl);
        }
        else
        {
            DbInitializer.InitializeProductionBaseline(
                dbContext,
                passwordHasher,
                app.Configuration["AdminBootstrap:Email"],
                app.Configuration["AdminBootstrap:Password"],
                financeDemoLaunchUrl);
        }
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

// 4. Enable WebSockets + CORS Middleware (WebSockets must be before CORS/Auth for SignalR negotiate)
app.UseWebSockets();
app.UseCors("KnownOrigins");

app.UseAuthentication();
app.UseMiddleware<SecurityMiddleware>();
app.UseAuthorization();

// 5. Map real-time MFA notifications and controller routes
app.MapHub<MfaHub>("/mfaHub").RequireAuthorization(policy =>
    policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
          .RequireAuthenticatedUser());
app.MapControllers();

// 6. Test Endpoint (to quickly check if API is running)
app.MapGet("/api/status", () => Results.Ok(new
{
    Status = "Online",
    System = "Safe Passage API",
    Commit = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "local",
    Timestamp = DateTime.UtcNow
}))
.WithName("GetSystemStatus");

app.Run();

static X509Certificate2? LoadCertificate(string? path, string? base64, string? password)
{
    if (!string.IsNullOrWhiteSpace(path))
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);

    if (!string.IsNullOrWhiteSpace(base64))
        return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64), password);

    return null;
}
