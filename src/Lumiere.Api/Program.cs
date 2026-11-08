using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumiere.Api;

if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
    DotEnv.LoadFromRepoRoot();

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<ICalendar>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var (calendarId, keyPath) = (config["GOOGLE_CALENDAR_ID"], config["GOOGLE_SERVICE_ACCOUNT_KEY_PATH"]);
    if (!string.IsNullOrWhiteSpace(calendarId) && !string.IsNullOrWhiteSpace(keyPath))
        return new GoogleCalendar(calendarId, keyPath);
    sp.GetRequiredService<ILogger<Program>>().LogWarning(
        "GOOGLE_CALENDAR_ID / GOOGLE_SERVICE_ACCOUNT_KEY_PATH not set: using the in-memory calendar (bookings are not persisted)");
    return new InMemoryCalendar();
});

builder.Services.AddSingleton(sp =>
{
    var configured = sp.GetRequiredService<IConfiguration>()["SLOT_SIGNING_KEY"];
    if (!string.IsNullOrEmpty(configured)) return new SigningKey(Encoding.UTF8.GetBytes(configured));
    if (!sp.GetRequiredService<IHostEnvironment>().IsDevelopment())
        throw new InvalidOperationException("SLOT_SIGNING_KEY is required");
    sp.GetRequiredService<ILogger<Program>>().LogWarning("SLOT_SIGNING_KEY not set: using an ephemeral random key (Development only)");
    return new SigningKey(RandomNumberGenerator.GetBytes(32));
});

builder.Services.AddSingleton(sp => new SlotIds(sp.GetRequiredService<SigningKey>().Bytes, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new BookingService(
    sp.GetRequiredService<ICalendar>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<SlotIds>(), sp.GetRequiredService<SigningKey>().Bytes));

var app = builder.Build();

if (string.IsNullOrEmpty(app.Configuration["TOOL_SHARED_SECRET"]))
{
    if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("TOOL_SHARED_SECRET is required");
    app.Logger.LogWarning("TOOL_SHARED_SECRET not set: every tool request will be rejected with 401");
}
// Resolve eagerly so configuration problems surface at startup, not during a call.
app.Services.GetRequiredService<BookingService>();

app.MapGet("/health", () => Results.Json(new { ok = true }));
app.MapToolEndpoints();

app.Run();

public sealed record SigningKey(byte[] Bytes);

public partial class Program;
