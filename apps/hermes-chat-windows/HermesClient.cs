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
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public string Provider { get; set; } = "";
    /// <summary>Модель, которая реально ответила. Совпадает с запрошенной не всегда:
    /// провайдер умеет переключаться на резервную.</summary>
    public string RuntimeModel { get; set; } = "";
    public string RouteSource { get; set; } = "";
    public double CreatedAt { get; set; }
    public double UpdatedAt { get; set; }
    public long DurationMs => UpdatedAt > CreatedAt ? (long)((UpdatedAt - CreatedAt) * 1000) : 0;
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

    /// <summary>Навыки с диска. Эндпоинт /v1/skills у шлюза падает500, поэтому читаем каталог.</summary>
    public List<SkillInfo> ReadSkills()
    {
        var list = new List<SkillInfo>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "skills");
        if (!Directory.Exists(root)) return list;
        foreach (var file in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
        {
            try
            {
                var head = File.ReadLines(file).Take(12).ToArray();
                string name = "", description = "";
                foreach (var line in head)
                {
                    if (line.StartsWith("name:", StringComparison.Ordinal)) name = line[5..].Trim().Trim('"');
                    else if (line.StartsWith("description:", StringComparison.Ordinal)) description = line[12..].Trim().Trim('"');
                    if (name.Length > 0 && description.Length > 0) break;
                }
                if (name.Length == 0) continue;
                var relative = Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/');
                list.Add(new SkillInfo { Name = name, Description = description, Category = relative });
            }
            catch (Exception) { /* нечитаемый SKILL.md не должен ломать весь список */ }
        }
        return list.OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();
    }

    /// <summary>Тулсеты, которые шлюз реально отдал агенту: /v1/toolsets.</summary>
    public async Task<List<ToolsetInfo>> ReadToolsetsAsync(CancellationToken cancel)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Config.NormalizedBase + "/v1/toolsets");
        Authorize(request);
        using var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) return new List<ToolsetInfo>();
        var body = await response.Content.ReadAsStringAsync(cancel);
        using var doc = JsonDocument.Parse(body);
        var list = new List<ToolsetInfo>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var item in data.EnumerateArray())
        {
            var tools = new List<string>();
            if (item.TryGetProperty("tools", out var toolArray) && toolArray.ValueKind == JsonValueKind.Array)
                foreach (var tool in toolArray.EnumerateArray())
                    if (tool.GetString() is { Length: > 0 } name) tools.Add(name);
            list.Add(new ToolsetInfo
            {
                Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Enabled = item.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True,
                Configured = item.TryGetProperty("configured", out var c) && c.ValueKind == JsonValueKind.True,
                Tools = tools
            });
        }
        return list;
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

    public async Task<string> StartRunAsync(string input, string sessionId, string model, CancellationToken cancel)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Config.NormalizedBase + "/v1/runs")
        {
            // model приходит из диалога: пусто — берётся модель по умолчанию из настроек
            Content = JsonContent(new { model = model.Length > 0 ? model : Config.Model, input, session_id = sessionId })
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

public sealed class ToolsetInfo
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Configured { get; set; }
    public List<string> Tools { get; set; } = new();
}