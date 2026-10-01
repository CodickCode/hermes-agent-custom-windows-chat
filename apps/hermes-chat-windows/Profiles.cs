using System.IO;
using System.Text.Json;

namespace HermesChat;

/// <summary>
/// Профильный агент направления. У каждого профиля свой «мозг» (Prompt), свои навыки,
/// своя модель и своя память (SessionKey). Диалог работает в контексте ровно одного профиля.
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = "profile-" + Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Новый профиль";
    public string ProjectId { get; set; } = "";
    public string ProjectTitle { get; set; } = "";
    /// <summary>Специализация: кто он и в чём его зона. Уходит в instructions шлюза.</summary>
    public string Prompt { get; set; } = "";
    /// <summary>Навыки, прикреплённые к профилю. Перечисляются в каждом запросе.</summary>
    public List<string> Skills { get; set; } = new();
    public string Model { get; set; } = "";
    /// <summary>Память профиля. Свой — значит профили не путают контекст между собой.</summary>
    public string SessionKey { get; set; } = "";
    public string Color { get; set; } = "#5B9CFF";
    public long CreatedAt { get; set; } = DateTimeOffset.Now.ToUnixTimeSeconds();
    public long LastUsedAt { get; set; }

    public bool IsBound => ProjectId.Length > 0;
    /// <summary>Кастомный шаблон ComboBox показывает объект как есть, поэтому текст
    /// элемента списка задаётся здесь, а не через DisplayMemberPath.</summary>
    public override string ToString() => Name;
}

public static class ProfileDefaults
{
    /// <summary>
    /// Направление → профиль. Специализация не выдумана: она собрана из незакрытых
    /// TODO снимка дерева, потому что именно они определяют зону работы.
    /// </summary>
    public static List<Profile> Seed()
    {
        return new List<Profile>
        {
            Make("CPA-трекер (rtb-velvetflux, 169)", "9", "#E0605A",
                "Ты ведёшь CPA-трекер: сверка реального расхода с трекерным, жёсткие лимиты, когортные отчёты.",
                "Расход и выплаты считаются по данным Kadam, а не по cpc трекера. Прежде чем предлагать TODO, назови цифру, на которой он строится."),
            Make("Fabra / Юкасса — модерация, РК Директ", "6", "#E0A33E",
                "Ты ведёшь Fabra: каталог услуг, модерация объявлений, РК Директ.",
                "Платёжный цикл и порядок в каталоге. Сначала сверяй факт по платежу и заказу, потом предлагай действие."),
            Make("Адалт-сайты и домены", "2", "#4FB477",
                "Ты ведёшь линию доменов: доходность, регистраторы, масштабирование.",
                "Доходность считается на домен в день. Не предлагай масштаб, пока не подтверждена доходность нового домена."),
            Make("Игра — Директ-закуп", "3", "#7BA7E8",
                "Ты ведёшь Директ-закуп игр: кампании, метрики, выбор донорской игры.",
                "Канал с нулём в центре денег не масштабируется: сначала метрика, потом бюджет."),
            Make("Парсер игр + конвейер заливки", "4", "#9E8CE8",
                "Ты ведёшь конвейер игр: парсер, сборку, заливку, QA.",
                "Конвейер: intake → сборка → QA → публикация. Сборка без проверки не считается выполненной."),
            Make("Spy AI", "8", "#63C7C4",
                "Ты ведёшь Spy AI: полнота выборки, оценка монет, фильтры.",
                "Сначала полнота и воспроизводимость выборки, потом любые выводы о качестве."),
            Make("JobLock + dolphin + акки сервисов", "5", "#E89A5B",
                "Ты ведёшь JobLock: аккаунты, прокси-матрицу, сплат-тесты.",
                "Сплат-тесты с автопроверкой по состоянию. Шаг, который нельзя проверить, не выполнен."),
            Make("CoDick — форк Hermes (Bootstrap-панель)", "1", "#6BCB77",
                "Ты ведёшь CoDick: форк Hermes и Bootstrap-панель.",
                "Разработка. Возвращай проверяемый результат и указывай, какой тест это подтверждает."),
            Make("BI-GATE — Task Manager для Windows", "10", "#5AC8E5",
                "Ты ведёшь BI-GATE: контракт данных между Windows и деревом.",
                "Пока контракт источника истины не зафиксирован, нижние слои писать рано."),
            Make("РАБОТА — время владельца", "11", "#B0B6C0",
                "Ты ведёшь учёт времени владельца по проектам.",
                "Факт часов, а не оценка. Каждый проект в дереве должен получить фактическое время.")
        };
    }

    private static Profile Make(string name, string projectId, string color, string prompt, string rule) => new()
    {
        Name = name,
        ProjectId = projectId,
        ProjectTitle = name,
        Color = color,
        SessionKey = "agent:profile:" + projectId + ":win",
        Prompt = prompt + "\n\nПравила работы:\n" + rule +
                 "\n- Бери один TODO за раз, не расширяй объём."
                 + "\n- Нужен доступ или решение владельца — спроси одним вопросом и остановись."
                 + "\n- Верни проверяемый результат: что сделано, чем проверено, что дальше."
    };
}

/// <summary>
/// Незакрытые TODO направления из снимка дерева. Профиль получает свою очередь
/// и поэтому может сам решать, какой узел брать следующим.
/// </summary>
public sealed class TreeBrief
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<TodoItem> Todo { get; set; } = new();

    public sealed class TodoItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
    }
}

public static class TreeBriefs
{
    private static readonly Dictionary<string, TreeBrief> ById = new();
    private static bool _loaded;

    /// <summary>Загружает снимок один раз. Отсутствие файла не считается ошибкой:
    /// профиль просто получит пустую очередь и скажет об этом.</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var path in SnapshotPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("projects", out var projects)) continue;
                foreach (var project in projects.EnumerateArray())
                {
                    var brief = new TreeBrief
                    {
                        ProjectId = project.TryGetProperty("id", out var id) ? Text(id) : "",
                        Title = project.TryGetProperty("title", out var title) ? title.GetString() ?? "" : ""
                    };
                    if (project.TryGetProperty("nodes", out var nodes))
                    {
                        foreach (var node in nodes.EnumerateArray())
                        {
                            var lane = node.TryGetProperty("lane", out var value) ? value.GetString() ?? "" : "";
                            if (!lane.Equals("todo", StringComparison.OrdinalIgnoreCase)) continue;
                            brief.Todo.Add(new TreeBrief.TodoItem
                            {
                                Id = node.TryGetProperty("id", out var nodeId) ? Text(nodeId) : "",
                                Title = node.TryGetProperty("title", out var nodeTitle) ? nodeTitle.GetString() ?? "" : "",
                                Body = node.TryGetProperty("body", out var nodeBody) ? nodeBody.GetString() ?? "" : ""
                            });
                        }
                    }
                    if (brief.ProjectId.Length == 0) continue;
                    ById[brief.ProjectId] = brief;
                    SourceFile = path;
                }
                return;
            }
            catch (Exception) { /* битый снимок — пробуем следующий путь */ }
        }
    }

    /// <summary>Дерево отдаёт id строкой, а не числом; принимаем оба вида.</summary>
    private static string Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetInt64().ToString(),
        JsonValueKind.String => element.GetString() ?? "",
        _ => ""
    };

    public static List<TreeBrief.TodoItem> TodoOf(string projectId)
    {
        Load();
        return projectId is not null && ById.TryGetValue(projectId, out var brief) ? brief.Todo : new List<TreeBrief.TodoItem>();
    }

    public static string TitleOf(string projectId)
    {
        Load();
        return projectId is not null && ById.TryGetValue(projectId, out var brief) ? brief.Title : "";
    }

    public static int TodoCount(string projectId) => TodoOf(projectId).Count;

    public static string SourceFile { get; private set; } = "не найден";

    /// <summary>
    /// Ищем снимок от самой вероятной точки к наименее вероятной: рядом с exe (при публикации
    /// он копируется туда csproj'ом), затем вверх по дереву папок — из bin/ и dist/ разная глубина.
    /// Раньше проверялись только два пути, и из bin/ снимок не находился вовсе.
    /// </summary>
    private static IEnumerable<string> SnapshotPaths()
    {
        var exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "tree-snapshot.json");

        var dir = new DirectoryInfo(exeDir);
        for (var depth = 0; depth < 7 && dir is not null; depth++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "tree-snapshot.json");
            if (File.Exists(direct)) { yield return direct; yield break; }
            var sibling = Path.Combine(dir.FullName, "pulsepilot", "tree-snapshot.json");
            if (File.Exists(sibling)) { yield return sibling; yield break; }
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HermesChat", "tree-snapshot.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HermesChat", "tree-snapshot.json");
    }
}