using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SafePassage.Api.Services;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class EmailBrandingTests
{
    [Fact]
    public async Task BrevoEmail_IncludesBrandedHtmlAndPlainText()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Brevo:ApiKey"] = "test-api-key",
                ["Brevo:FromAddress"] = "noreply@example.com",
                ["Frontend:BaseUrl"] = "https://portal.example.com"
            })
            .Build();
        var sender = new HttpApiActivationEmailSender(
            httpClient,
            configuration,
            Options.Create(new SmtpOptions()));

        await sender.SendActivationEmailAsync(
            "person@example.com",
            "<script>alert(1)</script>",
            "https://portal.example.com/activate?token=sample",
            requiresPasswordSetup: true,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var message = payload.RootElement;
        var html = message.GetProperty("htmlContent").GetString()!;
        var text = message.GetProperty("textContent").GetString()!;

        Assert.Contains("https://portal.example.com/favicon.png", html);
        Assert.Contains("Safe Passage", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("https://portal.example.com/activate?token=sample", text);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
