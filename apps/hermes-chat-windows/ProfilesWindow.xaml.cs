using System.Windows;
using System.Windows.Controls;

namespace HermesChat;

public partial class ProfilesWindow : Window
{
    private readonly Store _store;
    private Profile? _current;
    private bool _loading;

    public ProfilesWindow(Store store)
    {
        InitializeComponent();
        _store = store;
        List.ItemsSource = _store.Profiles;
        if (_store.Profiles.Count > 0) List.SelectedIndex = 0;
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not Profile profile) return;
        _current = profile;
        _loading = true;
        NameBox.Text = profile.Name;
        ProjectBox.Text = profile.ProjectId;
        ModelBox2.Text = profile.Model;
        SessionBox.Text = profile.SessionKey;
        PromptBox.Text = profile.Prompt;
        SkillsBox.Text = string.Join("\n", profile.Skills);
        _loading = false;
        ShowQueue();
    }

    /// <summary>Показывает, сколько TODO профиль реально получит — цифра из снимка, а не обещание.</summary>
    private void ShowQueue()
    {
        var id = ProjectBox.Text.Trim();
        if (id.Length == 0) { QueueNote.Text = "Универсальный агент: своей очереди нет."; return; }
        var count = TreeBriefs.TodoCount(id);
        var title = TreeBriefs.TitleOf(id);
        QueueNote.Text = count == 0
            ? "Снимок дерева не найден или направление пусто — агент не получит очереди."
            : $"Очередь направления: {count} TODO" + (title.Length > 0 ? $" · {title}" : "");
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var profile = new Profile
        {
            Name = "Новый профиль",
            Prompt = "Ты — профильный агент. Опиши здесь свою зону и правила.",
            SessionKey = "",
            Model = ""
        };
        _store.Profiles.Add(profile);
        List.Items.Refresh();
        List.SelectedItem = profile;
        _store.Save();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var name = _current.Name;
        // Диалоги не удаляем: профиль пропадает, а переписка остаётся как обычный разговор.
        foreach (var thread in _store.Threads.Where(t => t.ProfileId == _current.Id))
        {
            thread.ProfileId = "";
            thread.ProfileName = "";
        }
        _store.Profiles.Remove(_current);
        _current = null;
        List.Items.Refresh();
        if (List.Items.Count > 0) List.SelectedIndex = 0;
        _store.Save();
        _ = name;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        _current.Name = NameBox.Text.Trim().Length == 0 ? "Без имени" : NameBox.Text.Trim();
        _current.ProjectId = ProjectBox.Text.Trim();
        _current.ProjectTitle = _current.ProjectId.Length > 0 ? TreeBriefs.TitleOf(_current.ProjectId) : "";
        _current.Model = ModelBox2.Text.Trim();
        _current.SessionKey = SessionBox.Text.Trim().Length > 0
            ? SessionBox.Text.Trim()
            : _current.ProjectId.Length > 0 ? "agent:profile:" + _current.ProjectId + ":win" : "";
        _current.Prompt = PromptBox.Text;
        _current.Skills = SkillsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        List.Items.Refresh();
        _store.Save();
        ShowQueue();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        OnSave(sender, e);
        DialogResult = true;
    }
}