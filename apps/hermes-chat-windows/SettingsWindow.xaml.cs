using System.Windows;
using System.Windows.Media;

namespace HermesChat;

public partial class SettingsWindow : Window
{
    private readonly Store _store;

    public SettingsWindow(Store store)
    {
        InitializeComponent();
        _store = store;
        BaseBox.Text = store.Settings.BaseUrl;
        KeyBox.Password = store.Settings.ApiKey;
        SessionKeyBox.Text = store.Settings.DefaultSessionKey;
        ModelBox.Text = store.Settings.Model;
        ReasoningBox.IsChecked = store.Settings.ShowReasoning;
        ToolsBox.IsChecked = store.Settings.ShowTools;
        if (store.Settings.ApiKey.Length == 0) KeyNote.Text = "Пусто — приложение попробует взять ключ из %LOCALAPPDATA%\\hermes\\.env при запуске.";
    }

    private async void OnSaveAndProbe(object sender, RoutedEventArgs e)
    {
        var url = BaseBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            Fail("Адрес должен быть http:// или https://");
            return;
        }
        var key = KeyBox.Password.Trim();
        if (key.Length < 8) { Fail("Ключ слишком короткий."); return; }

        _store.Settings.BaseUrl = url.TrimEnd('/');
        _store.Settings.ApiKey = key;
        _store.Settings.DefaultSessionKey = SessionKeyBox.Text.Trim();
        _store.Settings.Model = ModelBox.Text.Trim().Length == 0 ? "hermes-agent" : ModelBox.Text.Trim();
        _store.Settings.ShowReasoning = ReasoningBox.IsChecked == true;
        _store.Settings.ShowTools = ToolsBox.IsChecked == true;
        _store.Save();

        ProbeNote.Text = "Проверяю…";
        var probe = await new HermesClient(_store.Settings).ProbeAsync(CancellationToken.None);
        if (!probe.Ok) { Fail(probe.Note); return; }
        _store.Save();
        DialogResult = true;
    }

    private void Fail(string note)
    {
        ProbeNote.Text = "Не получилось: " + note;
        ProbeNote.Foreground = (Brush)FindResource("Bad");
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // DialogResult работает только при ShowDialog — закрываемся так же, как от «Сохранить».
        DialogResult = false;
    }
}