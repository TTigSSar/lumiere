using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumiere.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumiere.Tests;

public sealed class HttpTests : IDisposable
{
    private const string Secret = "test-tool-secret";
    private readonly CapturingLoggerProvider _logs = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public HttpTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("TOOL_SHARED_SECRET", Secret)
            .UseSetting("SLOT_SIGNING_KEY", "test-signing-key")
            .ConfigureLogging(l => l.AddProvider(_logs).AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace))
            .ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(new FakeClock(T.MondayMorning));
                s.AddSingleton<ICalendar>(new InMemoryCalendar());
            }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tool-invocation.synthetic.json")))!.AsObject();

    private Task<HttpResponseMessage> Post(string path, string body, string? secret = Secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (secret is not null) req.Headers.Add("X-Tool-Secret", secret);
        req.Headers.Add("X-Suisse-Tool", "check_availability");
        req.Headers.Add("X-Suisse-Tool-Call-Id", "-1000000000.000001");
        return _client.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Health_is_ok()
    {
        var res = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await Json(res)).GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task Missing_or_wrong_secret_is_401(string? secret)
    {
        var res = await Post("/tools/check-availability", Fixture().ToJsonString(), secret);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        res = await Post("/tools/book", Fixture().ToJsonString(), secret);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"tool\":{}}")]
    [InlineData("{\"tool\":{\"arguments\":\"service\"}}")]
    [InlineData("[]")]
    public async Task Malformed_body_is_400(string body) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/tools/check-availability", body)).StatusCode);

    [Fact]
    public async Task Business_error_is_200_with_ok_false()
    {
        var body = Fixture();
        body["tool"]!["arguments"]!["service"] = "perm";
        var res = await Post("/tools/check-availability", body.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await Json(res);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("UNKNOWN_SERVICE", json.GetProperty("error_code").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("say").GetString()));
    }

    [Fact]
    public async Task Check_then_book_end_to_end_without_logging_the_body()
    {
        var check = await Json(await Post("/tools/check-availability", Fixture().ToJsonString()));
        Assert.True(check.GetProperty("ok").GetBoolean());
        Assert.Equal("Monday, 9 November 2026", check.GetProperty("today").GetString());
        var slot = check.GetProperty("slots")[0];
        Assert.Equal("Tuesday, 10 November at 09:00", slot.GetProperty("spoken_label").GetString());

        var body = Fixture();
        body["tool"]!["arguments"] = new JsonObject
        {
            ["slot_id"] = slot.GetProperty("slot_id").GetString(),
            ["first_name"] = "Testy",
            ["last_name"] = "Example",
            ["caller_phone"] = "079 000 00 01",
        };
        var book = await Json(await Post("/tools/book", body.ToJsonString()));
        Assert.True(book.GetProperty("ok").GetBoolean());
        Assert.StartsWith("You're booked for a men's cut on Tuesday, 10 November at 09:00", book.GetProperty("confirmation_text").GetString());

        var logs = string.Join("\n", _logs.Lines);
        Assert.Contains("tool book -> OK", logs);
        Assert.Contains("+41 79 *** ** 01", logs);
        Assert.DoesNotContain("SYNTHETIC-TRANSCRIPT-MARKER", logs);
        Assert.DoesNotContain("SYNTHETIC-PROMPT-MARKER", logs);
        Assert.DoesNotContain("+41790000001", logs);
        Assert.DoesNotContain("Testy", logs);
    }
}
