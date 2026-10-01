using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace HermesChat;

/// <summary>Одно сообщение в ленте чата. Роль: user | agent | system | tool.</summary>
public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    /// <summary>Живой текст, пока агент пишет. Финализируется в Text по run.completed.</summary>
    public string Streaming { get; set; } = "";
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "";        // ok | running | failed | stopped | partial
    public string Note { get; set; } = "";          // причина обрыва/ошибки
    public long CreatedAt { get; set; } = DateTimeOffset.Now.ToUnixTimeSeconds();
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string Model { get; set; } = "";
    public List<Attachment> Attachments { get; set; } = new();
    /// <summary>Хронология вызовов инструментов — как в ТГ, а не одна строка «последний инструмент».</summary>
    public List<ToolStep> Tools { get; set; } = new();

    public bool IsAgent => Role == "agent";
    public bool IsRunning => Status == "running";
    /// <summary>Что показать в ленте: во время стрима — накопленный текст, иначе финальный.</summary>
    public string Body => Streaming.Length > 0 ? Streaming : Text;
}

/// <summary>Один вызов инструмента в ходе ответа агента.</summary>
public sealed class ToolStep
{
    public string Tool { get; set; } = "";
    public string Preview { get; set; } = "";
    public string Result { get; set; } = "";
    public double Seconds { get; set; }
    public bool Error { get; set; }
    public bool Running { get; set; }
    public long At { get; set; } = DateTimeOffset.Now.ToUnixTimeMilliseconds();
}

public sealed class ChatThread : INotifyPropertyChanged
{
    private string _title = "Новый диалог";
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = "hermeschat-" + Guid.NewGuid().ToString("N")[..12];
    /// <summary>Переименовывается после первого сообщения — список обязан увидеть это,
    /// поэтому свойство с уведомлением, а не авто-свойство.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }
    public string SessionKey { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    /// <summary>Модель этого диалога. Пусто — модель по умолчанию из настроек.</summary>
    public string Model { get; set; } = "";
    /// <summary>Навыки, прикреплённые к диалогу: агент видит их как обязательные к применению.</summary>
    public List<string> Skills { get; set; } = new();
    public long UpdatedAt { get; set; } = DateTimeOffset.Now.ToUnixTimeSeconds();
    public long CreatedAt { get; set; } = DateTimeOffset.Now.ToUnixTimeSeconds();
    public int PendingAgentMessages { get; set; }

    public bool HasUnfinished => PendingAgentMessages > 0;
}

public sealed class ChatSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8642";
    public string ApiKey { get; set; } = "";
    public string DefaultSessionKey { get; set; } = "agent:hermeschat:win:dm:marti";
    public string Model { get; set; } = "hermes-agent";
    public bool ShowReasoning { get; set; } = true;
    public bool ShowTools { get; set; } = true;
    public int FontSize { get; set; } = 14;

    public string NormalizedBase => BaseUrl.TrimEnd('/');
    public bool Ready => ApiKey.Length >= 8 && Uri.TryCreate(NormalizedBase, UriKind.Absolute, out var u)
                          && u.Scheme is "http" or "https";
}