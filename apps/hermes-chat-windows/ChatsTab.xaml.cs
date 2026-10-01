using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HermesChat;

public partial class ChatsTab : UserControl
{
    /// <summary>Состояние общее для всех вкладок: один файл, одна правда.
    /// Свой Store здесь означал бы, что чаты и профили сохраняются互相но и затирают друг друга.</summary>
    public Store State { get; set; } = null!;
    private Store _store => State;
    private HermesClient _client = null!;
    public HermesClient Client => _client;
    private ChatThread? _thread;
    private CancellationTokenSource? _cts;
    private ChatMessage? _live;
    private readonly List<Attachment> _pending = new();
    private List<SkillInfo> _skills = new();
    private bool _loadingModel;
    private bool _loadingProfile;
    private readonly ObservableCollection<ChatThread> _threads = new();
    private bool _suppressListEvent;

    /// <summary>Профиль, выбранный во вкладке профилей: открыть с ним диалог.</summary>
    public event Action<string>? RequestThreadWithProfile;


    public ChatsTab()
    {
        // Разметка обязана быть загружена до Init(): без этого все именованные
        // элементы (ModelBox, ThreadList) остаются null и Init падает.
        InitializeComponent();
    }

    /// <summary>Старт вкладки. Вызывается оболочкой после присвоения State,
    /// поэтому загрузка состояния тут не нужна: файл уже прочитан один раз.</summary>
    public void Init()
    {
        if (_store.Settings.ApiKey.Length == 0) AutoFillKeyFromHermesEnv();
        _client = new HermesClient(_store.Settings);
        // Наблюдаемая коллекция: иначе ListBox не покажет добавленный диалог —
        // ItemsSource переустановка на ту же ссылку List<T> не обновляет.
        _loadingModel = true;
        foreach (var choice in ModelCatalog.Defaults) ModelBox.Items.Add(choice);
        ModelBox.SelectedIndex = 0;
        _loadingModel = false;
        _skills = _client.ReadSkills();
        FillProfiles();
        SkillsBtn.ToolTip = _skills.Count == 0
            ? "Навыки не найдены в каталоге Hermes"
            : $"{_skills.Count} навыков доступно";
        foreach (var thread in _store.Threads) _threads.Add(thread);
        ThreadList.ItemsSource = _threads;
        ThreadList.SelectedIndex = _threads.Count > 0 ? 0 : -1;
        PreviewKeyDown += OnWindowPaste;
        Loaded += async (_, _) => await ProbeAsync();
        Unloaded += (_, _) => Shutdown();
    }

    /// <summary>Ключ берём из .env Hermes, чтобы приложение работало сразу после установки.</summary>
    private void AutoFillKeyFromHermesEnv()
    {
        try
        {
            var env = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", ".env");
            if (!File.Exists(env)) return;
            var match = Regex.Match(File.ReadAllText(env), @"^API_SERVER_KEY=(.+)$", RegexOptions.Multiline);
            if (match.Success) _store.Settings.ApiKey = match.Groups[1].Value.Trim();
        }
        catch (Exception error) { CrashLog.Write("Автоподстановка ключа: " + error.Message); }
    }

    private void Shutdown()
    {
        // Отменяем ДО dispose: отменённый CTS, который уже освобождён, бросает
        // ObjectDisposedException именно на закрытии окна — и это выглядит как «баг кнопки».
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            cts.Cancel();
            try { cts.Dispose(); } catch (ObjectDisposedException) { }
        }
        _store.Save();
    }

    // ---------- шлюз ----------

    private async Task ProbeAsync()
    {
        if (_client is null || !_store.Settings.Ready)
        {
            StatusText.Text = "Не настроено";
            StatusText.Foreground = Find("Bad");
            StatusNote.Text = "Укажи адрес и ключ в настройках.";
            return;
        }
        StatusText.Text = "Проверяю…";
        StatusText.Foreground = Find("Warn");
        StatusNote.Text = _store.Settings.NormalizedBase;
        ProbeBtn.IsEnabled = false;
        try
        {
            var probe = await _client.ProbeAsync(CancellationToken.None);
            StatusText.Text = probe.Ok ? "Шлюз доступен" : "Шлюз недоступен";
            StatusText.Foreground = Find(probe.Ok ? "Good" : "Bad");
            StatusNote.Text = probe.Note;
        }
        finally { ProbeBtn.IsEnabled = true; }
    }

    private void OnProbe(object sender, RoutedEventArgs e) => _ = ProbeAsync();

    private Brush Find(string key) => (Brush)FindResource(key);

    // ---------- диалоги ----------

    private void OnThreadSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvent) return;
        if (ThreadList.SelectedItem is not ChatThread thread) return;
        _thread = thread;
        HeaderText.Text = thread.Title;
        LoadThreadProfile();
        LoadThreadModel();
        Render();
        UpdateStats();
    }

    private void OnNewThread(object sender, RoutedEventArgs e)
    {
        var thread = new ChatThread { Title = "Новый диалог " + DateTime.Now.ToString("HH:mm") };
        _store.Threads.Insert(0, thread);
        _suppressListEvent = true;
        _threads.Insert(0, thread);
        ThreadList.SelectedItem = thread;
        _suppressListEvent = false;
        _thread = thread;
        HeaderText.Text = thread.Title;
        LoadThreadProfile();
        LoadThreadModel();
        Render();
        UpdateStats();
        Input.Focus();
    }

    // ---------- лента ----------

    private void Render()
    {
        Feed.Children.Clear();
        if (_thread is null) return;
        if (_thread.Messages.Count == 0)
        {
            Feed.Children.Add(new TextBlock
            {
                Text = "Спроси агента о чём угодно. Он видит твою машину: файлы, команды, веб.",
                Foreground = Find("Dim"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 24, 0, 0)
            });
            return;
        }
        foreach (var message in _thread.Messages) Feed.Children.Add(BuildBubble(message));
        ScrollToEnd();
    }

    private Border BuildBubble(ChatMessage message)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        var meta = new TextBlock
        {
            Text = RoleName(message)
                 + (message.Status == "running" ? " · " + StatusLine(message)
                    : message.Status == "ok" && message.Model.Length > 0 ? " · " + message.Model : ""),
            FontSize = 11,
            Foreground = Find(message.Status switch { "running" => "Warn", "failed" => "Bad", "partial" => "Warn", "stopped" => "Dim", _ => "Dim" }),
            Margin = new Thickness(2, 0, 0, 4)
        };
        panel.Children.Add(meta);

        var body = new TextBlock
        {
            Text = message.Body.Length > 0 ? message.Body : "…",
            TextWrapping = TextWrapping.Wrap,
            FontSize = _store.Settings.FontSize,
            LineHeight = _store.Settings.FontSize * 1.45,
            Foreground = message.IsAgent ? Find("Fg") : Find("Accent")
        };
        var bubble = new Border
        {
            Background = message.IsAgent ? Find("Panel") : Find("Bg"),
            BorderBrush = message.IsAgent ? Find("Line") : Find("Accent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Child = body,
            HorizontalAlignment = message.IsAgent ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            MaxWidth = 760
        };
        panel.Children.Add(bubble);

        if (message.Attachments.Count > 0)
        {
            var strip = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var file in message.Attachments)
                strip.Children.Add(BuildAttachmentChip(file));
            panel.Children.Add(strip);
        }

        if (message.WaitingApproval)
            panel.Children.Add(BuildApprovalBlock(message));

        if (message.Steers.Count > 0)
            foreach (var steer in message.Steers)
                panel.Children.Add(new TextBlock
                {
                    Text = "твоя подсказка: " + steer,
                    FontSize = 11,
                    Foreground = Find("Accent"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 4, 0, 0)
                });

        if (message.Tools.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Работа агента",
                FontSize = 11,
                Foreground = Find("Dim"),
                Margin = new Thickness(2, 10, 0, 3)
            });
            foreach (var step in message.Tools) panel.Children.Add(BuildToolRow(step));
        }

        if (message.Note.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = message.Note,
                FontSize = 11,
                Foreground = Find("Warn"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 0, 0)
            });
        if (message.InputTokens > 0 || message.OutputTokens > 0)
            panel.Children.Add(new TextBlock
            {
                Text = UsageLine(message),
                FontSize = 10,
                Foreground = Find("Dim"),
                TextWrapping = TextWrapping.Wrap,
                ToolTip = message.RouteSource.Length > 0 ? "маршрут: " + message.RouteSource : "",
                Margin = new Thickness(2, 5, 0, 0)
            });
        return new Border { Child = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>Живая строка состояния: видно, что агент делает, а не только «пишет».</summary>
    private static string StatusLine(ChatMessage message)
    {
        if (message.WaitingApproval) return "ждёт твоего решения";
        if (message.Tools.LastOrDefault() is { Running: true } step) return "работает: " + step.Tool;
        if (message.Streaming.Length > 0) return "пишет…";
        return "думает…";
    }

    /// <summary>Блок одобрения: без него запуск висит в waiting_for_approval молча.</summary>
    private FrameworkElement BuildApprovalBlock(ChatMessage message)
    {
        var box = new StackPanel();
        box.Children.Add(new TextBlock
        {
            Text = "Агент просит разрешить: " + message.ApprovalTool,
            FontSize = 12,
            Foreground = Find("Warn"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (message.ApprovalCommand.Length > 0)
            box.Children.Add(new Border
            {
                Background = Find("Bg"),
                BorderBrush = Find("Warn"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock
                {
                    Text = message.ApprovalCommand,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = Find("Fg"),
                    TextWrapping = TextWrapping.Wrap
                }
            });

        var row = new WrapPanel();
        foreach (var choice in message.ApprovalChoices)
        {
            var label = choice switch
            {
                "once" => "Разрешить",
                "session" => "Разрешить до конца сессии",
                "always" => "Разрешить всегда",
                "deny" => "Запретить",
                _ => choice
            };
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource(choice == "deny" ? "Btn" : "PrimaryBtn"),
                Margin = new Thickness(0, 0, 8, 0),
                Tag = message.Id + "|" + choice
            };
            button.Click += OnApproveClick;
            row.Children.Add(button);
        }
        box.Children.Add(row);
        return new Border
        {
            Background = Find("Panel"),
            BorderBrush = Find("Warn"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Child = box
        };
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var parts = tag.Split('|');
        var message = _thread?.Messages.FirstOrDefault(m => m.Id == parts[0]);
        if (message is null || _client is null || message.RunId.Length == 0) return;
        var choice = parts[1];
        try
        {
            await _client.ApproveAsync(message.RunId, choice,
                message.ApprovalRequestId.Length > 0 ? message.ApprovalRequestId : null);
            message.WaitingApproval = false;
            message.Note = "Ответ отправлен: " + choice;
            Render();
        }
        catch (Exception error)
        {
            CrashLog.Write("Одобрение: " + error);
            MessageBox.Show("Не удалось ответить: " + error.Message, "HermesChat",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Подсказка в идущий запуск. Пустое поле означает обычную отправку.</summary>
    private async void OnSteerSend(object sender, RoutedEventArgs e)
    {
        var text = SteerInput.Text.Trim();
        if (text.Length == 0 || _live is null || _client is null || _live.RunId.Length == 0) return;
        try
        {
            await _client.SteerAsync(_live.RunId, text);
            _live.Steers.Add(text);
            SteerInput.Clear();
            _live.Note = "Подсказка отправлена агенту";
            Render();
        }
        catch (Exception error)
        {
            CrashLog.Write("Steer: " + error);
            MessageBox.Show("Подсказку не приняли: " + error.Message, "HermesChat",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private FrameworkElement BuildToolRow(ToolStep step)
    {
        var row = new StackPanel { Margin = new Thickness(2, 1, 0, 1), Tag = step };
        var head = new TextBlock
        {
            Text = step.Running
                ? "• " + step.Tool + " — работает…"
                : step.Error
                    ? "• " + step.Tool + " — ошибка" + (step.Seconds > 0 ? $" ({step.Seconds:0.#} с)" : "")
                    : "• " + step.Tool + (step.Seconds > 0 ? $" — {step.Seconds:0.#} с" : " — готово"),
            FontSize = 12,
            Foreground = step.Error ? Find("Bad") : step.Running ? Find("Warn") : Find("Dim"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        row.Children.Add(head);
        var detail = step.Running ? step.Preview : step.Result.Length > 0 ? step.Result : step.Preview;
        if (detail.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = "    " + Trim(detail, 220),
                FontSize = 11,
                Foreground = Find("Dim"),
                TextWrapping = TextWrapping.Wrap
            });
        return row;
    }

    /// <summary>На лету перерисовывает только строки инструментов, не трогая текст ответа.</summary>
    private void RefreshToolRows(Panel rows, ChatMessage message)
    {
        if (!_store.Settings.ShowTools || message.Tools.Count == 0) return;
        // Строки лежат в фиксированном хвосте пузыря: пересобираем их, чтобы не искать по дереву.
        var existing = rows.Children.OfType<StackPanel>()
            .Where(child => child.Tag is ToolStep).ToList();
        foreach (var child in existing) rows.Children.Remove(child);
        foreach (var step in message.Tools) rows.Children.Add(BuildToolRow(step));
    }

    private FrameworkElement BuildAttachmentChip(Attachment file)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        if (file.PreviewBase64 is { Length: > 0 })
        {
            try
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(Convert.FromBase64String(file.PreviewBase64));
                image.DecodePixelHeight = 64;
                image.EndInit();
                image.Freeze();
                inner.Children.Add(new Image
                {
                    Source = image,
                    Height = 44,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = file.Path
                });
            }
            catch (Exception) { /* превью не получилось — покажем только имя */ }
        }
        inner.Children.Add(new TextBlock
        {
            Text = file.Name + "  " + file.SizeText,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = Find("Fg"),
            ToolTip = file.Path
        });
        return new Border
        {
            Background = Find("Bg"),
            BorderBrush = Find("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Child = inner
        };
    }

    private static string RoleName(ChatMessage message) => message.Role switch
    { "user" => "Ты", "agent" => "Hermes", _ => message.Role };

    /// <summary>Обновляет только пузырь активного ответа — не пересоздаёт ленту на каждом токене.</summary>
    private void PatchLive()
    {
        if (_live is null || _thread is null) return;
        var index = _thread.Messages.IndexOf(_live);
        if (index < 0) return;
        var item = Feed.Children[index] as Border;
        if (item?.Child is not Border panel) return;
        var stack = panel.Child as StackPanel;
        if (stack is null || stack.Children.Count < 2) return;
        if (stack.Children[1] is Border bubble && bubble.Child is TextBlock body)
        {
            body.Text = _live.Body.Length > 0 ? _live.Body : "…";
            body.Foreground = Find("Fg");
        }
        if (stack.Children[0] is TextBlock meta)
            meta.Text = "Hermes · " + StatusLine(_live);
        RefreshToolRows(stack, _live);
        UpdateStats();
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { FeedScroll.ScrollToEnd(); } catch (Exception) { /* лента ещё не измерена */ }
        }), DispatcherPriority.Background);
    }

    // ---------- отправка ----------

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) return;
        e.Handled = true;
        OnSend(sender, e);
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        var text = Input.Text.Trim();
        if ((text.Length == 0 && _pending.Count == 0) || _thread is null || _client is null) return;
        if (_cts is not null) return;                       // один активный run на диалог
        if (!_store.Settings.Ready)
        {
            MessageBox.Show("Сначала укажи адрес и ключ шлюза в настройках.", "HermesChat");
            return;
        }

        Input.Clear();
        var attachments = _pending.ToList();
        _pending.Clear();
        ShowAttachments();
        var payload = ComposeInput(text, attachments, _thread.Skills);
        var user = new ChatMessage { Role = "user", Text = payload, Attachments = attachments };
        var agent = new ChatMessage { Role = "agent", Status = "running" };
        _thread.Messages.Add(user);
        _thread.Messages.Add(agent);
        if (_thread.Title.StartsWith("Новый диалог", StringComparison.Ordinal))
        {
            var basis = text.Length > 0 ? text : attachments[0].Name;
            _thread.Title = basis.Length > 60 ? basis[..60] + "…" : basis;
        }
        ThreadList.Items.Refresh();
        UpdateHeader();
        _live = agent;
        Render();
        SetBusy(true);

        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var runId = await _client.StartRunAsync(payload, _thread.Id, ResolveModel(_thread),
                BuildProfileInstructions(_thread) ?? "", ProfileSessionKey(_thread), cts.Token);
            agent.RunId = runId;
            await _client.StreamAsync(runId,
                item => Dispatcher.BeginInvoke(new Action(() => Apply(item))),
                _ => { }, cts.Token);
            await SettleAsync(agent, runId, cts.Token);
        }
        catch (OperationCanceledException)
        {
            agent.Status = "stopped";
            agent.Note = "Остановлено тобой.";
        }
        catch (Exception error)
        {
            agent.Status = "failed";
            agent.Note = error is HermesException ? error.Message : "Ошибка: " + error.Message;
            CrashLog.Write("Отправка: " + error);
        }
        finally
        {
            if (agent.Streaming.Length > 0 && agent.Text.Length == 0) agent.Text = agent.Streaming;
            agent.Streaming = "";
            if (agent.Status == "running") { agent.Status = "partial"; agent.Note = "Поток событий закрылся без ответа."; }
            _live = null;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            try { cts.Dispose(); } catch (ObjectDisposedException) { }
            _store.Save();
            SetBusy(false);
            Render();
            Input.Focus();
        }
    }

    /// <summary>Применяет событие потока к живому сообщению.</summary>
    private void Apply(RunEvent item)
    {
        if (_live is null) return;
        switch (item.Event)
        {
            case "tool.started" when _store.Settings.ShowTools:
                _live.Tools.Add(new ToolStep
                {
                    Tool = item.Tool,
                    Preview = Trim(item.Preview, 300),
                    Running = true
                });
                break;
            case "tool.completed" when _store.Settings.ShowTools:
            {
                // Ищем незакрытый вызов того же инструмента: события не гарантируют парность по имени.
                var step = _live.Tools.LastOrDefault(s => s.Tool == item.Tool && s.Running);
                if (step is null)
                    _live.Tools.Add(new ToolStep { Tool = item.Tool });
                else
                {
                    step.Running = false;
                    step.Error = item.Error;
                    step.Seconds = item.Duration;
                    step.Result = Trim(item.Preview, 400);
                }
                break;
            }
            case "message.delta":
                _live.Streaming += item.Delta;
                break;
            case "message.interim" when _store.Settings.ShowReasoning && !item.AlreadyStreamed:
                _live.Streaming += "\n" + item.Text;
                break;
            case "approval.request":
                // Запуск стоит и ждёт решения. Без явного блока с кнопками он молчал бы
                // вечно, и пользователь не понимал бы, почему «агент завис».
                _live.WaitingApproval = true;
                _live.ApprovalCommand = item.Command.Length > 0 ? item.Command : item.Preview;
                _live.ApprovalTool = item.ToolName.Length > 0 ? item.ToolName : item.Tool;
                _live.ApprovalRequestId = item.RequestId;
                _live.ApprovalChoices = item.Choices.Count > 0
                    ? new List<string>(item.Choices)
                    : new List<string> { "once", "deny" };
                _live.Note = "Нужно твоё решение: " + Trim(_live.ApprovalCommand, 160);
                break;
            case "approval.resolved":
            case "approval.decided":
                _live.WaitingApproval = false;
                _live.Note = "Решение принято: " + Trim(item.Choice.Length > 0 ? item.Choice : "подтверждено", 80);
                break;
            case "reasoning.available" when _store.Settings.ShowReasoning:
                _live.Note = "Размышление: " + Trim(item.Text, 140);
                break;
            case "run.completed":
                ApplyUsage(_live, item);
                _live.Text = item.Output;
                _live.Status = "ok";
                _live.ToolCount = _live.Tools.Count;
                _live.Note = "";
                break;
            case "run.failed":
                _live.Status = "failed";
                _live.Note = Trim(item.Message.Length > 0 ? item.Message : "Агент сообщил об ошибке.", 300);
                break;
            case "run.cancelled":
                _live.Status = "stopped";
                _live.Note = "Остановлено.";
                break;
            case "run.interrupted":
                _live.Status = "partial";
                _live.Note = "Gateway прервал запуск.";
                break;
        }
        if (item.Event.StartsWith("run.", StringComparison.Ordinal)) PatchLive();
        else PatchLive();
    }

    /// <summary>Переносит usage/runtime из события или из ответа опроса в сообщение.</summary>
    private static void ApplyUsage(ChatMessage message, RunEvent item)
    {
        message.InputTokens = item.InputTokens;
        message.OutputTokens = item.OutputTokens;
        message.CacheReadTokens = item.CacheReadTokens;
        message.CacheWriteTokens = item.CacheWriteTokens;
        if (item.RuntimeModel.Length > 0) message.Model = item.RuntimeModel;
        if (item.Provider.Length > 0) message.Provider = item.Provider;
        if (item.RouteSource.Length > 0) message.RouteSource = item.RouteSource;
        if (item.DurationMs > 0) message.DurationMs = item.DurationMs;
    }

    /// <summary>Строка учёта: сколько контекста ушло, сколько вернулось, что с кэшем и кто ответил.</summary>
    private static string UsageLine(ChatMessage message)
    {
        var parts = new List<string>
        {
            $"контекст {message.InputTokens:N0}",
            $"ответ {message.OutputTokens:N0}",
            $"всего {message.InputTokens + message.OutputTokens:N0}"
        };
        if (message.CacheReadTokens > 0) parts.Add($"кэш чтение {message.CacheReadTokens:N0}");
        if (message.CacheWriteTokens > 0) parts.Add($"кэш запись {message.CacheWriteTokens:N0}");
        if (message.ToolCount > 0) parts.Add($"инструментов {message.ToolCount}");
        if (message.DurationMs > 0) parts.Add($"{message.DurationMs / 1000.0:0.#} с");
        if (message.Provider.Length > 0) parts.Add(message.Provider + "/" + message.Model);
        return string.Join(" · ", parts);
    }

    /// <summary>Итог по диалогу — то, что в консоли видно как расход сессии.</summary>
    private void UpdateStats()
    {
        if (_thread is null) { StatsText.Text = ""; return; }
        var turns = _thread.Messages.Where(m => m.IsAgent && m.Status == "ok").ToList();
        if (turns.Count == 0)
        {
            StatsText.Text = "Диалог пуст — расхода пока нет.";
            return;
        }
        var last = turns[^1];
        var total = turns.Sum(m => m.InputTokens + m.OutputTokens);
        var cache = turns.Sum(m => m.CacheReadTokens);
        var totalTools = turns.Sum(m => m.ToolCount);
        var worst = turns.Max(m => m.InputTokens);
        StatsText.Text =
            $"ходов {turns.Count} · всего токенов {total:N0} · из них из кэша {cache:N0}" +
            $" · инструментов {totalTools} · пик контекста {worst:N0} · последний: {UsageLine(last)}";
        StatsText.ToolTip = string.Join("\n",
            turns.Select(m => $"{Time(m.CreatedAt)}  {UsageLine(m)}"));
    }

    private static string Time(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Добирает финальный статус опросом, если поток закрылся без терминального события.</summary>
    private async Task SettleAsync(ChatMessage agent, string runId, CancellationToken cancel)
    {
        if (_client is null || runId.Length == 0) return;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (cancel.IsCancellationRequested || agent.Status is "ok" or "failed" or "stopped" or "partial") return;
            RunEvent state;
            try { state = await _client.StatusAsync(runId, cancel); }
            catch (Exception) { return; }
            if (state.Status is "completed" or "failed" or "cancelled" or "interrupted")
            {
                Apply(state);
                if (state.Status == "completed") { ApplyUsage(agent, state); agent.Status = "ok"; agent.Text = state.Output; agent.ToolCount = agent.Tools.Count; }
                else if (state.Status == "failed") { agent.Status = "failed"; agent.Note = Trim(state.Message, 300); }
                else if (state.Status == "cancelled") { agent.Status = "stopped"; agent.Note = "Остановлено."; }
                else { agent.Status = "partial"; agent.Note = "Запуск прерван на стороне шлюза."; }
                return;
            }
            await Task.Delay(1200, cancel);
        }
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        var cts = _cts;
        if (cts is null || _client is null || _live?.RunId is not { Length: > 0 } runId) return;
        StopBtn.IsEnabled = false;
        try { await _client.StopAsync(runId); }
        finally { StopBtn.IsEnabled = true; cts.Cancel(); }
    }

    private void SetBusy(bool busy)
    {
        SendBtn.IsEnabled = !busy;
        SendBtn.Content = busy ? "Агент работает" : "Отправить";
        StopBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SteerBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            StopBtn.IsEnabled = true;
            if (!SteerInput.IsFocused) SteerInput.Focus();
        }
        else SteerInput.Clear();
    }

    private static string Trim(string value, int size) =>
        value.Length <= size ? value : value[..size] + "…";

    // ---------- модель диалога ----------

    /// <summary>Показывает модель диалога. Неизвестный идентификатор не подменяется молча:
    /// он остаётся в поле, помеченный как свой маршрут, — иначе пользователь не видит,
    /// что реально отправит в шлюз.</summary>
    private void LoadThreadModel()
    {
        if (_thread is null) return;
        _loadingModel = true;
        var wanted = _thread.Model.Length > 0 ? _thread.Model : _store.Settings.Model;
        var index = IndexOfModel(wanted);
        if (index < 0)
        {
            ModelBox.Items.Insert(0, new ModelChoice { Id = wanted, Label = wanted, Note = "свой маршрут" });
            index = 0;
        }
        ModelBox.SelectedItem = ModelBox.Items[index];
        ModelBox.Text = wanted;               // в поле ввода — чистый id, он и уходит в шлюз
        _loadingModel = false;
        UpdateHeader();
    }

    /// <summary>Профили в списке плюс явный пункт «без профиля»: универсальный агент —
    /// не ошибка, а осознанный выбор, поэтому он должен быть видимой опцией.</summary>
    private void FillProfiles()
    {
        _loadingProfile = true;
        ProfileBox.Items.Clear();
        ProfileBox.Items.Add(new Profile { Id = "", Name = "Универсальный (без профиля)" });
        foreach (var profile in _store.Profiles)
            ProfileBox.Items.Add(new Profile { Id = profile.Id, Name = profile.Name });
        _loadingProfile = false;
    }

    private void LoadThreadProfile()
    {
        _loadingProfile = true;
        var index = 0;
        for (var i = 0; i < ProfileBox.Items.Count; i++)
            if (ProfileBox.Items[i] is Profile choice && choice.Id == _thread?.ProfileId) { index = i; break; }
        ProfileBox.SelectedIndex = index;
        _loadingProfile = false;
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || _thread is null || ProfileBox.SelectedItem is not Profile choice) return;
        _thread.ProfileId = choice.Id;
        _thread.ProfileName = choice.Id.Length > 0
            ? _store.Profiles.FirstOrDefault(p => p.Id == choice.Id)?.Name ?? ""
            : "";
        if (choice.Id.Length > 0)
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == choice.Id);
            if (profile is not null)
            {
                profile.LastUsedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
                // Навыки профиля переходят в диалог: иначе они были бы мёртвым текстом в настройках.
                foreach (var skill in profile.Skills.Where(s => !_thread.Skills.Contains(s)))
                    _thread.Skills.Add(skill);
            }
        }
        _store.Save();
        UpdateHeader();
    }

    /// <summary>Открывает или создаёт диалог, привязанный к профилю оркестратора.</summary>
    public void OpenWithProfile(string profileId)
    {
        var existing = _threads.FirstOrDefault(t => t.ProfileId == profileId);
        if (existing is null)
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == profileId);
            existing = new ChatThread
            {
                ProfileId = profileId,
                ProfileName = profile?.Name ?? "Профиль",
                Title = profile is not null && profile.ProjectId.Length > 0
                    ? profile.ProjectTitle
                    : profile?.Name ?? "Диалог профиля"
            };
            _store.Threads.Insert(0, existing);
            _threads.Insert(0, existing);
        }
        ThreadList.SelectedItem = existing;
        Input.Focus();
    }

    /// <summary>Просит оболочку открыть вкладку профилей.</summary>
    public event Action? RequestOpenProfiles;

    private void OnOpenProfiles(object sender, RoutedEventArgs e) => RequestOpenProfiles?.Invoke();

    private int IndexOfModel(string id)
    {
        for (var i = 0; i < ModelBox.Items.Count; i++)
            if (ModelBox.Items[i] is ModelChoice choice && choice.Id == id) return i;
        return -1;
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingModel || _thread is null) return;
        if (ModelBox.SelectedItem is not ModelChoice choice || choice.Id.Length == 0) return;
        ApplyModel(choice.Id);
    }

    /// <summary>Пользователь вписал модель руками. Пустая строка — вернуть модель по умолчанию.</summary>
    private void OnModelTyped(object sender, RoutedEventArgs e)
    {
        if (_loadingModel || _thread is null) return;
        var typed = ModelBox.Text.Trim();
        if (typed.Length == 0) { LoadThreadModel(); return; }
        ApplyModel(typed);
    }

    private void ApplyModel(string id)
    {
        if (_thread is null) return;
        var index = IndexOfModel(id);
        if (index < 0)
        {
            // Неизвестный id добавляем в список, чтобы он не потерялся при переключении диалогов.
            ModelBox.Items.Insert(0, new ModelChoice { Id = id, Label = id, Note = "свой маршрут" });
            _loadingModel = true;
            ModelBox.SelectedItem = ModelBox.Items[0];
            _loadingModel = false;
        }
        _thread.Model = id;
        _store.Save();
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        var requested = _thread is null ? _store.Settings.Model : ResolveModel(_thread);
        var profile = _thread is null ? null : ProfileOf(_thread);
        var todo = profile?.IsBound == true ? TreeBriefs.TodoCount(profile.ProjectId) : 0;
        var skills = _thread?.Skills.Count ?? 0;
        HeaderText.Text = (_thread?.Title ?? "Диалог")
            + (profile is not null ? "  ·  " + profile.Name : "")
            + "  ·  " + requested
            + (todo > 0 ? $"  ·  TODO в очереди: {todo}" : "")
            + (skills > 0 ? $"  ·  навыков: {skills}" : "");
        // Если провайдер переключился на резервную модель, это видно только по runtime.model —
        // молча показывать запрошенную значило бы врать о том, кто ответил и сколько стоил ответ.
        var served = _thread?.Messages.LastOrDefault(m => m.IsAgent && m.Model.Length > 0);
        if (served is not null && served.Model.Length > 0 && served.Model != requested)
            HeaderText.Text += $"  →  ответила {served.Model}";
        HeaderText.ToolTip = HeaderText.Text;
    }

    // ---------- навыки ----------

    private void OnSkills(object sender, RoutedEventArgs e)
    {
        if (_thread is null) return;
        if (_skills.Count == 0)
        {
            MessageBox.Show("В каталоге Hermes не найдено ни одного SKILL.md. Проверь %LOCALAPPDATA%\\hermes\\skills.",
                "HermesChat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new SkillsWindow(_skills, _thread.Skills) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        _store.Save();
        UpdateHeader();
    }

    // ---------- вложения ----------

    private void ShowAttachments()
    {
        AttachBar.ItemsSource = _pending;
        AttachBar.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddAttachment(Attachment attachment)
    {
        if (_pending.Count >= 10)
        {
            MessageBox.Show("Больше десяти вложений в одно сообщение не берём — агент запутается.",
                "HermesChat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _pending.Add(attachment);
        ShowAttachments();
    }

    private void OnPasteImage(object sender, RoutedEventArgs e) => TryPaste();

    private void TryPaste()
    {
        try { AddAttachment(Attachments.FromClipboard()); }
        catch (Exception error) { MessageBox.Show(error.Message, "HermesChat", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Выбери файлы для агента" };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        foreach (var file in dialog.FileNames)
        {
            try { AddAttachment(Attachments.FromPath(file)); }
            catch (Exception error) { CrashLog.Write("Вложение: " + error.Message); MessageBox.Show(error.Message, "HermesChat"); }
        }
    }

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Attachment attachment })
        {
            _pending.Remove(attachment);
            ShowAttachments();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var file in files)
        {
            try { AddAttachment(Attachments.FromPath(file)); }
            catch (Exception error) { CrashLog.Write("Drag&drop: " + error.Message); MessageBox.Show(error.Message, "HermesChat"); }
        }
    }

    /// <summary>Ctrl+V — скриншот из буфера. Не перехватываем обычную вставку текста.</summary>
    private void OnWindowPaste(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (!Clipboard.ContainsImage()) return;
        e.Handled = true;
        TryPaste();
    }

    /// <summary>
    /// Инструкции профиля для шлюза. К специализации добавляется очередь его направления:
    /// без неё агент-«менеджер» не знает, из чего выбирать следующий шаг.
    /// </summary>
    private string? BuildProfileInstructions(ChatThread thread)
    {
        var profile = thread.ProfileId.Length > 0 ? _store.Profiles.FirstOrDefault(p => p.Id == thread.ProfileId) : null;
        if (profile is null) return null;
        var parts = new List<string> { profile.Prompt };
        if (profile.Skills.Count > 0)
            parts.Add("Обязательные навыки этого профиля: " + string.Join(", ", profile.Skills) + ". Примени их.");
        var todo = TreeBriefs.TodoOf(profile.ProjectId);
        if (todo.Count > 0)
        {
            parts.Add("Незакрытые TODO твоего направления ("
                + (profile.ProjectTitle.Length > 0 ? profile.ProjectTitle : "направление") + "):");
            parts.Add(string.Join("\n", todo.Select(item => $"[{item.Id}] {item.Title}".Trim())));
        }
        else if (profile.ProjectId.Length > 0)
        {
            parts.Add("Снимок дерева не найден — своей очереди TODO у тебя нет. Скажи об этом прямо, а не выдумывай номера.");
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>Ключ памяти профиля. У профиля он свой — иначе направления путают контекст.</summary>
    private string ProfileSessionKey(ChatThread thread)
    {
        if (thread.ProfileId.Length == 0) return "";
        var profile = _store.Profiles.FirstOrDefault(p => p.Id == thread.ProfileId);
        return profile?.SessionKey ?? "";
    }

    private Profile? ProfileOf(ChatThread thread) =>
        thread.ProfileId.Length > 0 ? _store.Profiles.FirstOrDefault(p => p.Id == thread.ProfileId) : null;

    /// <summary>Модель диалога: профиль важнее диалога, диалог важнее настроек.</summary>
    private string ResolveModel(ChatThread thread)
    {
        if (thread.ProfileId.Length > 0)
        {
            var profile = _store.Profiles.FirstOrDefault(p => p.Id == thread.ProfileId);
            if (profile is not null && profile.Model.Length > 0) return profile.Model;
        }
        return thread.Model.Length > 0 ? thread.Model : _store.Settings.Model;
    }

    /// <summary>Путь в тексте запроса: агент читает файл сам, это и есть «отправка файла».</summary>
    private static string ComposeInput(string text, IReadOnlyList<Attachment> attachments, IReadOnlyList<string> skills)
    {
        var parts = new List<string>();
        if (skills.Count > 0) parts.Add("Обязательные навыки этого диалога: " + string.Join(", ", skills) + ". Примени их.");
        if (text.Length > 0) parts.Add(text);
        if (attachments.Count > 0)
        {
            parts.Add("Прикреплённые файлы (прочитай их по путям):");
            foreach (var file in attachments)
                parts.Add($"- {file.Name} ({file.SizeText}) → {file.Path}");
            parts.Add("Прежде чем отвечать, посмотри каждый файл.");
        }
        return string.Join("\n\n", parts);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_store);
        window.ShowDialog();
        _client = new HermesClient(_store.Settings);
        Render();
        _ = ProbeAsync();
    }
}