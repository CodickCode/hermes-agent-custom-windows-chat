namespace HermesChat;

/// <summary>Модель, доступная в диалоге. Шлюз принимает и произвольные id — их можно вписать вручную.</summary>
public sealed class ModelChoice
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public static class ModelCatalog
{
    /// <summary>Проверено живьём: /v1/models отдаёт только hermes-agent, но произвольный
    /// model в /v1/runs проходит и маршрутизируется как raw_request. Поэтому список
    /// предложений живёт здесь, а не берётся у шлюза.</summary>
    public static readonly ModelChoice[] Defaults =
    {
        new() { Id = "hermes-agent",                Label = "Hermes — текущая модель шлюза" },
        new() { Id = "stealth/space-bunny-alpha",   Label = "Space Bunny Alpha — быстро" },
        new() { Id = "anthropic/claude-sonnet-4",    Label = "Claude Sonnet 4 — сильное рассуждение" },
        new() { Id = "openai/gpt-4.1",               Label = "GPT-4.1" },
        new() { Id = "google/gemini-2.5-pro",        Label = "Gemini 2.5 Pro" },
        new() { Id = "deepseek/deepseek-v4-flash",   Label = "DeepSeek V4 Flash — дёшево" }
    };

    public static bool Known(string id) => Defaults.Any(m => m.Id == id);
}

/// <summary>Навык агента, прочитанный с диска. Эндпоинт /v1/skills у шлюза падает 500,
/// поэтому список берём из каталога Hermes — он и есть источник истины для агента.</summary>
public sealed class SkillInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
}

/// <summary>Вложение: скриншот из буфера, перетащенный файл или выбранный через диалог.</summary>
public sealed class Attachment
{
    public string Name { get; set; } = "";
    /// <summary>Абсолютный путь — агент читает файл с диска своей машины (шлюз локальный).</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public bool IsImage { get; set; }
    /// <summary>Снимок экрана, снятый с буфера: показываем рядом с именем, чтобы видеть, что отправлено.</summary>
    public string? PreviewBase64 { get; set; }

    public string SizeText => Size switch
    {
        < 1024 => $"{Size} Б",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} КБ",
        _ => $"{Size / (1024.0 * 1024.0):0.#} МБ"
    };
}
