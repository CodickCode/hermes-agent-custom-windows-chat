using System.IO;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

namespace HermesChat;

public sealed class HermesException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Событие из SSE-потока /v1/runs/{id}/events.</summary>
public sealed class RunEvent
{
    public string Event { get; set; } = "";
    public string RunId { get; set; } = "";
    public long Seq { get; set; }
    public string Delta { get; set; } = "";
    public string Text { get; set; } = "";
    public string Output { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Preview { get; set; } = "";
    public bool Error { get; set; }
    public double Duration { get; set; }
    public bool AlreadyStreamed { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Клиент api_server Hermes: создание run, SSE событий, остановка, опрос статуса.</summary>
public sealed class HermesClient(ChatSettings settings)
{
    public ChatSettings Config { get; } = settings;

    private HttpClient NewClient() => new() { Timeout = TimeSpan.FromSeconds(120) };

    private void Authorize(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
        if (Config.DefaultSessionKey.Length > 0)
            request.Headers.TryAddWithoutValidation("X-Hermes-Session-Key", Config.DefaultSessionKey);
    }

    public async Task<RunProbe> ProbeAsync(CancellationToken cancel)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Config.NormalizedBase + "/v1/capabilities");
        Authorize(request);
        try
        {
            using var response = await http.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new RunProbe(false, "Ключ отклонён (401). Проверь API_SERVER_KEY.", 401);
            if (!response.IsSuccessStatusCode)
                return new RunProbe(false, $"HTTP {(int)response.StatusCode} от /v1/capabilities.", (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(cancel);
            using var doc = JsonDocument.Parse(body);
            var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            return new RunProbe(true, "Шлюз отвечает. Модель: " + (model.Length == 0 ? "?" : model), 200);
        }
        catch (TaskCanceledException) { return new RunProbe(false, "Таймаут. Шлюз не отвечает.", 0); }
        catch (HttpRequestException error) { return new RunProbe(false, "Шлюз недоступен: " + error.Message, 0); }
    }

    public async Task<string> StartRunAsync(string threadId, string input, string sessionId, CancellationToken cancel)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Config.NormalizedBase + "/v1/runs")
        {
            Content = JsonContent(new { model = Config.Model, input, session_id = sessionId })
        };
        Authorize(request);
        using var response = await http.SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw new HermesException(Explain(response.StatusCode, body), (int)response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("run_id").GetString()
               ?? throw new HermesException("Шлюз не вернул run_id.");
    }

    /// <summary>Стримит событий. onEvent вызывается на UI-потоке через переданный делегат.</summary>
    public async Task StreamAsync(string runId, Action<RunEvent> onEvent, Action<string> onTerminal, CancellationToken cancel)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Config.NormalizedBase}/v1/runs/{runId}/events");
        Authorize(request);
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancel);
            throw new HermesException(Explain(response.StatusCode, body), (int)response.StatusCode);
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancel);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..].Trim();
            if (payload.Length == 0) continue;
            RunEvent? parsed;
            try { parsed = JsonSerializer.Deserialize<RunEvent>(payload, JsonOpts); }
            catch (JsonException) { continue; }
            if (parsed is null) continue;
            onEvent(parsed);
            if (parsed.Event is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted")
            {
                onTerminal(parsed.Event);
                return;
            }
        }
        onTerminal("closed");   // поток закрылся без терминального события — финальный статус узнаем опросом
    }

    public async Task<RunEvent> StatusAsync(string runId, CancellationToken cancel)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Config.NormalizedBase}/v1/runs/{runId}");
        Authorize(request);
        using var response = await http.SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw new HermesException(Explain(response.StatusCode, body), (int)response.StatusCode);
        return JsonSerializer.Deserialize<RunEvent>(body, JsonOpts) ?? new RunEvent();
    }

    public async Task StopAsync(string runId)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Config.NormalizedBase}/v1/runs/{runId}/stop")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        Authorize(request);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await http.SendAsync(request, cts.Token); }
        catch (Exception) { /* остановка best-effort: пользователь уже нажал «стоп», результат узнаем опросом */ }
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value, JsonOpts), Encoding.UTF8, "application/json");

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string Explain(HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                    detail = message.GetString() ?? "";
                else detail = error.ToString();
            }
        }
        catch (JsonException) { detail = body.Length > 200 ? body[..200] : body; }
        return $"HTTP {(int)status} от шлюза." + (detail.Length > 0 ? " " + detail : "");
    }
}

public sealed record RunProbe(bool Ok, string Note, int Status);