using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HermesChat;

/// <summary>
/// Вкладка «Внешние шлюзы»: реестр агентов на других машинах, проверка связи
/// и тестовая задача. Проверка capabilities ничего не значит, пока агент
/// не выполнил хоть что-то, поэтому шагов два.
/// </summary>
public partial class GatewaysTab : UserControl
{
    public Store State { get; set; } = null!;
    private Gateway? _current;
    private CancellationTokenSource? _probe;

    public GatewaysTab()
    {
        InitializeComponent();
    }

    public void Load()
    {
        GatewayList.ItemsSource = State.Gateways;
        if (State.Gateways.Count > 0 && GatewayList.SelectedItem is null)
            GatewayList.SelectedIndex = 0;
        FillProfiles();
    }

    private void FillProfiles()
    {
        ProfileBox.Items.Clear();
        ProfileBox.Items.Add(new Profile { Id = "", Name = "— не закреплён —" });
        foreach (var profile in State.Profiles)
            ProfileBox.Items.Add(new Profile { Id = profile.Id, Name = profile.Name });
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (GatewayList.SelectedItem is not Gateway gateway) return;
        _current = gateway;
        NameBox.Text = gateway.Name;
        UrlBox.Text = gateway.Url;
        TokenBox.Text = gateway.Token;
        NoteBox.Text = gateway.Note;
        KindBox.SelectedIndex = gateway.Kind == "hermes" ? 1 : 0;
        ProfileBox.SelectedItem = ProfileBox.Items.OfType<Profile>()
            .FirstOrDefault(p => p.Id == gateway.ProfileId) ?? ProfileBox.Items[0];
        Diagnose();
        ResultText.Text = gateway.Status == "не проверен"
            ? "Шлюз ещё не проверялся."
            : $"{gateway.Status} — {gateway.StatusNote}\\nпроверено: {gateway.CheckedText}";
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e) => Diagnose();

    private void Diagnose()
    {
        var url = UrlBox?.Text ?? "";
        var note = GatewayClient.Diagnose(url);
        DiagnoseText.Text = note;
        DiagnoseText.Foreground = (Brush)FindResource(url.Length == 0 ? "Dim" : "Warn");
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var gateway = new Gateway { Name = "Шлюз " + (State.Gateways.Count + 1) };
        State.Gateways.Add(gateway);
        GatewayList.Items.Refresh();
        GatewayList.SelectedItem = gateway;
        State.Save();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        State.Gateways.Remove(_current);
        _current = null;
        GatewayList.Items.Refresh();
        if (GatewayList.Items.Count > 0) GatewayList.SelectedIndex = 0;
        State.Save();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _current.Name = NameBox.Text.Trim().Length == 0 ? "Без имени" : NameBox.Text.Trim();
        _current.Url = UrlBox.Text.Trim();
        _current.Token = TokenBox.Text.Trim();
        _current.Note = NoteBox.Text;
        _current.Kind = KindBox.SelectedIndex == 1 ? "hermes" : "bridge";
        _current.ProfileId = ProfileBox.SelectedItem is Profile profile ? profile.Id : "";
        GatewayList.Items.Refresh();
        State.Save();
    }

    private async void OnProbe(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        OnSave(sender, e);
        if (_current.ReadyError.Length > 0)
        {
            ResultText.Text = "Проверять пока нечего: " + _current.ReadyError;
            return;
        }
        await RunProbeAsync(_current, sendTask: false);
    }

    /// <summary>Тестовая задача: доказательство, что агент не только отвечает, но и работает.</summary>
    private async void OnRunTest(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        OnSave(sender, e);
        if (_current.ReadyError.Length > 0)
        {
            ResultText.Text = "Проверять пока нечего: " + _current.ReadyError;
            return;
        }
        await RunProbeAsync(_current, sendTask: true);
    }

    private async void OnProbeAll(object sender, RoutedEventArgs e)
    {
        if (State.Gateways.Count == 0) { ResultText.Text = "Реестр пуст."; return; }
        foreach (var gateway in State.Gateways.ToList())
        {
            if (gateway.ReadyError.Length > 0)
            {
                gateway.Status = "не проверен";
                gateway.StatusNote = gateway.ReadyError;
                gateway.CheckedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
                continue;
            }
            await RunProbeAsync(gateway, sendTask: false, quiet: true);
        }
        GatewayList.Items.Refresh();
        ResultText.Text = "Проверено шлюзов: " + State.Gateways.Count
            + ", доступно: " + State.Gateways.Count(g => g.Reachable);
    }

    private async Task RunProbeAsync(Gateway gateway, bool sendTask, bool quiet = false)
    {
        var token = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _probe, token);
        if (previous is not null)
        {
            try { previous.Cancel(); previous.Dispose(); } catch (ObjectDisposedException) { }
        }
        if (!quiet)
        {
            ResultText.Text = "Проверяю…";
            ResultOutput.Text = "";
        }
        try
        {
            var client = new GatewayClient(gateway);
            var capabilities = await client.CapabilitiesAsync(token.Token);
            gateway.LatencyMs = capabilities.Milliseconds;
            gateway.Status = capabilities.Ok ? "доступен" : "недоступен";
            gateway.StatusNote = capabilities.Detail;
                        if (!capabilities.Ok)
            {
                gateway.CheckedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
                if (!quiet) { ResultText.Text = "Связь не удалась: " + capabilities.Detail; ResultOutput.Text = ""; }
                return;
            }

            var probe = capabilities;
            if (sendTask)
            {
                var task = await client.RunTaskAsync(
                    "Ответь ровно одним словом: работаю. Больше ничего не пиши.",
                    "probe-" + gateway.Id, token.Token);
                probe = task;
                gateway.StatusNote = capabilities.Detail + "; задача: " + task.Detail;
                gateway.Model = task.Model.Length > 0 ? task.Model : capabilities.Model;
            }
            else
            {
                gateway.Model = capabilities.Model;
            }
            gateway.CheckedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
            State.Save();
            if (!quiet)
            {
                ResultText.Text = $"{probe.Stage}: {probe.Detail}"
                    + (probe.Milliseconds > 0 ? $"  ·  {probe.Milliseconds} мс" : "")
                    + (probe.InputTokens > 0 ? $"\nтокены: вход {probe.InputTokens}, выход {probe.OutputTokens}" : "");
                ResultOutput.Text = probe.Output;
            }
        }
        catch (OperationCanceledException)
        {
            gateway.Status = gateway.Reachable ? gateway.Status : "недоступен";
            gateway.StatusNote = "проверка отменена";
        }
        catch (Exception error)
        {
            gateway.Status = "недоступен";
            gateway.StatusNote = "ошибка: " + error.Message;
            gateway.CheckedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (!quiet) { ResultText.Text = "Ошибка проверки: " + error.Message; ResultOutput.Text = ""; }
        }
        finally
        {
            var done = Interlocked.Exchange(ref _probe, null);
            if (done is not null && !ReferenceEquals(done, token)) { try { done.Dispose(); } catch (ObjectDisposedException) { } }
        }
    }
}