using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using MailVault.Core;
using MailVault.Core.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace MailVault.Tests;

/// <summary>
/// Prüft den kompletten Gesprächsablauf mit der Claude-Schnittstelle gegen einen lokalen Test-Server, der sich wie die
/// Messages-API verhält: Er "denkt" (Signatur), ruft Werkzeuge auf, liest deren echte Ergebnisse aus dem Testarchiv und
/// antwortet daraus. So wird geprüft, was MailVault sendet (Werkzeuge, Werkzeugergebnisse, Denkblöcke, Beta-Kopfzeile,
/// Ersatzmodell) und wie es Antworten, Belege, Ablehnungen und Fehler verarbeitet – ohne echten API-Schlüssel.
/// </summary>
public class AiProtocolTests
{
    readonly ITestOutputHelper _out;
    public AiProtocolTests(ITestOutputHelper output) => _out = output;

    sealed class FakeClaude : IAsyncDisposable
    {
        readonly WebApplication _app;
        public List<(Dictionary<string, string> Headers, JsonObject Body)> Requests { get; } = new();
        public Func<JsonObject, int, (int Status, JsonObject Response)> Respond { get; set; } = (_, _) => (500, new JsonObject());
        public string Url { get; }

        public FakeClaude()
        {
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.UseUrls("http://127.0.0.1:0");
            _app = b.Build();
            _app.MapPost("/v1/messages", async (HttpContext ctx) =>
            {
                var body = (await JsonNode.ParseAsync(ctx.Request.Body))!.AsObject();
                int n;
                var headers = ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
                lock (Requests) { Requests.Add((headers, body)); n = Requests.Count; }
                var (status, resp) = Respond(body, n);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(resp.ToJsonString());
            });
            _app.StartAsync().GetAwaiter().GetResult();
            Url = _app.Urls.First();
        }

        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
    }

    static JsonObject Message(string stop, params JsonNode[] content) => new()
    {
        ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..8],
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = "claude-opus-5-5",
        ["content"] = new JsonArray(content),
        ["stop_reason"] = stop,
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 50 },
    };

    static JsonObject ToolUse(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };
    static JsonObject Text(string t) => new() { ["type"] = "text", ["text"] = t };
    static JsonObject Thinking(string sig) => new() { ["type"] = "thinking", ["thinking"] = "", ["signature"] = sig };

    /// <summary>Letzter tool_result-Inhalt aus der Anfrage.</summary>
    static (string ToolUseId, string Content, bool IsError) LastToolResult(JsonObject body)
    {
        var last = body["messages"]!.AsArray().Last()!;
        var block = last["content"]!.AsArray().First(b => (string?)b!["type"] == "tool_result")!;
        var content = block["content"] switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray a => string.Concat(a.Select(x => (string?)x!["text"])),
            _ => "",
        };
        return ((string)block["tool_use_id"]!, content, (bool?)block["is_error"] ?? false);
    }

    static (MailArchive Archive, TempDir Tmp, long InvoiceId) Archive()
    {
        var tmp = new TempDir();
        var a = MailArchive.Open(tmp.Path);
        var f = a.EnsureFolderPath(new[] { "anna@example.org", "INBOX" });
        var m = new MimeMessage { Subject = "Ihre Rechnung März 2025", Date = new DateTimeOffset(2025, 3, 2, 8, 0, 0, TimeSpan.Zero) };
        m.From.Add(MailboxAddress.Parse("Telekom <rechnung@telekom.de>"));
        m.To.Add(MailboxAddress.Parse("anna@example.org"));
        m.Body = new TextPart("plain") { Text = "Guten Tag, Ihr Rechnungsbetrag beträgt 49,99 EUR und ist fällig am 15.04.2025." };
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        var id = a.StoreMessage(f, ms.ToArray())!.Value;
        a.StoreMessage(f, ArchiveTests.MakeMessage("Gartenfest", "Wer bringt Salat mit?"));
        return (a, tmp, id);
    }

    [Fact]
    public async Task Full_tool_conversation_is_protocol_correct()
    {
        var (archive, tmp, invoiceId) = Archive();
        using var _ = tmp;
        using var __ = archive;
        await using var claude = new FakeClaude();
        claude.Respond = (body, n) => n switch
        {
            // 1) denken + suchen
            1 => (200, Message("tool_use", Thinking("sig-1"), ToolUse("toolu_1", "search_emails", new JsonObject { ["query"] = "telekom rechnung" }))),
            // 2) aus dem echten Suchergebnis die ID nehmen und die E-Mail lesen
            2 => (200, Message("tool_use", Text("Ich lese die Rechnung."), ToolUse("toolu_2", "read_email",
                    new JsonObject { ["id"] = JsonNode.Parse(LastToolResult(body).Content)!["results"]![0]!["id"]!.GetValue<long>() }))),
            // 3) aus dem echten Text Betrag und Frist entnehmen
            _ => (200, Message("end_turn", Text(Answer(LastToolResult(body).Content)))),
        };

        var settings = new AiSettings { Model = AiModels.Default };
        var client = new AnthropicClient { ApiKey = "sk-ant-test", BaseUrl = claude.Url };
        var ai = new AiAssistant(settings, new AiArchiveTools(archive, maxReads: 5), client);
        var progress = new List<string>();
        var answer = await ai.AskAsync("Wie hoch ist die Telekom-Rechnung und wann ist sie fällig?", new SyncProgress(progress), default);
        _out.WriteLine(answer.Text);
        foreach (var p in progress) _out.WriteLine("Fortschritt: " + p);

        Assert.Contains("49,99 EUR", answer.Text);
        Assert.Contains("15.04.2025", answer.Text);
        Assert.Equal(new[] { invoiceId }, answer.ReferencedIds);
        Assert.Equal(2, answer.ToolCalls);
        Assert.Equal(1, answer.EmailsRead);
        Assert.Equal(3000, answer.InputTokens);
        Assert.Contains(progress, p => p.StartsWith("Suche: telekom rechnung"));
        Assert.Contains(progress, p => p.StartsWith("Liest E-Mail #"));

        Assert.Equal(3, claude.Requests.Count);
        var (headers, first) = claude.Requests[0];
        // SDK 13.x sendet den Schlüssel als Bearer-Token (API-Version 2026-10-21), ältere als x-api-key
        Assert.True(headers.GetValueOrDefault("authorization") == "Bearer sk-ant-test" || headers.GetValueOrDefault("x-api-key") == "sk-ant-test");
        Assert.Contains("server-side-fallback-2026-07-01", headers["anthropic-beta"]);
        Assert.Equal("claude-opus-5-5", (string)first["model"]!);
        Assert.Equal("default", (string)first["fallbacks"]!);
        Assert.True((int)first["max_tokens"]! >= 4000);
        Assert.Contains("[#123]", first["system"]!.ToJsonString());
        var tools = first["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
        Assert.Equal(new[] { "search_emails", "read_email", "list_folders", "contact_stats", "email_volume", "find_related" }, tools);
        Assert.All(first["tools"]!.AsArray(), t => Assert.Equal("object", (string)t!["input_schema"]!["type"]!));

        // Anfrage 2: Denkblock mit Signatur unverändert zurück, Werkzeugergebnis zur richtigen ID
        var second = claude.Requests[1].Body;
        var msgs = second["messages"]!.AsArray();
        Assert.Equal(3, msgs.Count);
        var assistant = msgs[1]!["content"]!.AsArray();
        Assert.Equal("thinking", (string)assistant[0]!["type"]!);
        Assert.Equal("sig-1", (string)assistant[0]!["signature"]!);
        Assert.Equal("toolu_1", (string)assistant[1]!["id"]!);
        var (useId, content, isError) = LastToolResult(second);
        Assert.Equal("toolu_1", useId);
        Assert.False(isError);
        Assert.Contains("49,99", content);  // Ausschnitt im Suchergebnis

        // Rückfrage nutzt den Verlauf
        claude.Respond = (body, n) => (200, Message("end_turn", Text("Fällig am 15.04.2025 [#" + invoiceId + "].")));
        var follow = await ai.AskAsync("Und wann genau?", null, default);
        Assert.Equal(7, claude.Requests[3].Body["messages"]!.AsArray().Count); // 3 + Antwort + Frage ... siehe Verlauf
        Assert.Equal(new[] { invoiceId }, follow.ReferencedIds);
    }

    static string Answer(string readResult)
    {
        var mail = JsonNode.Parse(readResult)!;
        var text = (string)mail["text"]!;
        var amount = Regex.Match(text, @"\d+,\d{2} EUR").Value;
        var due = Regex.Match(text, @"\d{2}\.\d{2}\.\d{4}").Value;
        return $"## Telekom-Rechnung\n- Betrag: **{amount}**\n- Fällig am: {due}\n\nQuelle: [#{(long)mail["id"]!}] und [#999999]";
    }

    [Fact]
    public async Task Tool_errors_are_returned_to_claude_not_thrown()
    {
        var (archive, tmp, _) = Archive();
        using var t = tmp;
        using var a = archive;
        await using var claude = new FakeClaude();
        claude.Respond = (body, n) => n == 1
            ? (200, Message("tool_use", ToolUse("toolu_x", "read_email", new JsonObject { ["id"] = 424242 })))
            : (200, Message("end_turn", Text("Diese E-Mail gibt es nicht.")));
        var ai = new AiAssistant(new AiSettings(), new AiArchiveTools(archive), new AnthropicClient { ApiKey = "k", BaseUrl = claude.Url });
        var answer = await ai.AskAsync("Lies E-Mail 424242", null, default);
        var (_, content, isError) = LastToolResult(claude.Requests[1].Body);
        Assert.True(isError);
        Assert.Contains("gibt es nicht", content);
        Assert.Equal("Diese E-Mail gibt es nicht.", answer.Text);
    }

    [Fact]
    public async Task Refusal_ends_turn_without_dangling_tool_use()
    {
        var (archive, tmp, _) = Archive();
        using var t = tmp;
        using var a = archive;
        await using var claude = new FakeClaude();
        claude.Respond = (body, n) => n == 1
            ? (200, Message("refusal", ToolUse("toolu_r", "search_emails", new JsonObject { ["query"] = "x" })))
            : (200, Message("end_turn", Text("Gern.")));
        var ai = new AiAssistant(new AiSettings(), new AiArchiveTools(archive), new AnthropicClient { ApiKey = "k", BaseUrl = claude.Url });
        var r1 = await ai.AskAsync("?", null, default);
        Assert.Contains("abgelehnt", r1.Text);
        await ai.AskAsync("Neue Frage", null, default);
        // Der Verlauf enthält keinen tool_use ohne Ergebnis (sonst lehnt die API die nächste Anfrage ab)
        var msgs = claude.Requests[^1].Body["messages"]!.AsArray();
        Assert.DoesNotContain(msgs, m => m!["content"] is JsonArray c && c.Any(b => (string?)b!["type"] == "tool_use"));
    }

    [Theory]
    [InlineData(401, "authentication_error", "API-Schlüssel wurde abgelehnt")]
    [InlineData(429, "rate_limit_error", "ausgelastet")]
    [InlineData(400, "invalid_request_error", "abgelehnt")]
    public async Task Api_errors_become_understandable_messages(int status, string type, string expected)
    {
        var (archive, tmp, _) = Archive();
        using var t = tmp;
        using var a = archive;
        await using var claude = new FakeClaude();
        claude.Respond = (_, _) => (status, new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = "test" } });
        var client = new AnthropicClient { ApiKey = "k", BaseUrl = claude.Url, MaxRetries = 0 };
        var ai = new AiAssistant(new AiSettings(), new AiArchiveTools(archive), client);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ai.AskAsync("?", null, default));
        Assert.Contains(expected, ex.Message);
    }

    sealed class SyncProgress : IProgress<string>
    {
        readonly List<string> _list;
        public SyncProgress(List<string> list) => _list = list;
        public void Report(string value) { lock (_list) _list.Add(value); }
    }
}
