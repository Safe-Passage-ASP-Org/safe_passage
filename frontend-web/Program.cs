using System.IO.Compression;
using Microsoft.AspNetCore.HttpOverrides;
using SafePassage.Web.Components;
using SafePassage.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
var apiBaseUrl = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5143/";
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute) });
builder.Services.AddScoped<AdminApiClient>();
builder.Services.AddScoped<SecurityAlertCountService>();

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Online",
    System = "Safe Passage Web",
    Commit = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "local",
    Timestamp = DateTime.UtcNow
}));

app.MapGet("/downloads/safepassage-extension.zip", (IWebHostEnvironment env) =>
{
    var candidates = new[]
    {
        Path.Combine(env.ContentRootPath, "extension"),
        Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "extension"))
    };
    var extensionDir = candidates.FirstOrDefault(Directory.Exists);
    if (extensionDir is null)
        return Results.NotFound(new { message = "Browser extension package not found." });

    using var memoryStream = new MemoryStream();
    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var filePath in Directory.EnumerateFiles(extensionDir, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.GetRelativePath(extensionDir, filePath).Replace('\\', '/');
            archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
        }
    }

    return Results.File(memoryStream.ToArray(), "application/zip", "safepassage-extension.zip");
});

app.MapGet("/downloads/safepassage-mobile.apk", (IConfiguration config) =>
{
    var apkUrl = config["Mobile:ApkDownloadUrl"];
    if (string.IsNullOrWhiteSpace(apkUrl))
        apkUrl = "https://github.com/Safe-Passage-ASP-Org/safe_passage/releases/download/mobile-latest/safepassage-mobile.apk";
    return Results.Redirect(apkUrl, permanent: false);
});

app.Run();
