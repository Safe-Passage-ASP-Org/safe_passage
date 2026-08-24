var builder = WebApplication.CreateBuilder(args);

// 1. Enable CORS so Blazor & React Native can communicate with this API
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// 2. Add Controller and OpenAPI support
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// 3. Configure HTTP pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// 4. Enable CORS Middleware (Must be before Authentication/Authorization)
app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

// 5. Map Controller routes
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