using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HermesChat;

/// <summary>
/// Оболочка приложения: три раздела поверх одного общего состояния.
/// Чаты, профили оркестраторов и внешние шлюзы читают и пишут один store,
/// поэтому диалог может быть привязан к профилю, а шлюз — к профилю же.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Store _store = new();
    private string _current = "";

    public MainWindow()
    {
        InitializeComponent();
        _store.Load();
        _store.RefreshProfiles();

        Chats.State = _store;
        Profiles.State = _store;
        Gateways.State = _store;
        Profiles.RequestThread += OpenThreadWithProfile;
        Chats.RequestThreadWithProfile += OpenThreadWithProfile;
        Chats.RequestOpenProfiles += () => Show("profiles");

        Chats.Init();
        Profiles.Load();
        Gateways.Load();
        Show("chats");
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag) Show(tag);
    }

    private void Show(string tag)
    {
        _current = tag;
        Chats.Visibility = tag == "chats" ? Visibility.Visible : Visibility.Collapsed;
        Profiles.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        Gateways.Visibility = tag == "gateways" ? Visibility.Visible : Visibility.Collapsed;
        // Активная вкладка подсвечивается, остальные гаснут — иначе при трёх
        // одинаковых кнопках непонятно, какой раздел открыт.
        Mark(TabChats, tag == "chats");
        Mark(TabProfiles, tag == "profiles");
        Mark(TabGateways, tag == "gateways");
        // Вкладки читают состояние при показе: список мог измениться, пока их не было видно.
        if (tag == "profiles") Profiles.Load();
        if (tag == "gateways") Gateways.Load();
    }

    private static void Mark(Button button, bool active) =>
        button.Background = (Brush)Application.Current.FindResource(active ? "PanelHi" : "Panel");

    /// <summary>Открыть диалог с выбранным профилем — из вкладки профилей.</summary>
    private void OpenThreadWithProfile(string profileId)
    {
        Show("chats");
        Chats.OpenWithProfile(profileId);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _store.Save();
        base.OnClosing(e);
    }
}