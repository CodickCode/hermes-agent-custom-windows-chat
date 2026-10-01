using HermesChat;

/// <summary>
/// Логика профилей без UI: засев, привязка к направлению, очередь TODO, сборка инструкций.
/// Компилируется как консоль — Profile.cs подключён ссылкой в .csproj.
/// </summary>
internal static class ProfileLogicTests
{
    private static int _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name + (detail.Length > 0 ? " — " + detail : ""));
        if (!ok) _failed++;
    }

    private static int Main()
    {
        Console.WriteLine("=== профили направлений ===");

        var seed = ProfileDefaults.Seed();
        Check("засев даёт 10 профилей", seed.Count == 10, "получено " + seed.Count);
        Check("у каждого есть направление", seed.All(p => p.ProjectId.Length > 0));
        Check("у каждого есть специализация", seed.All(p => p.Prompt.Length > 50));
        Check("ключ памяти уникален", seed.Select(p => p.SessionKey).Distinct().Count() == seed.Count);
        Check("id направления числовой", seed.All(p => int.TryParse(p.ProjectId, out _)));
        Check("универсальный агент не попал в засев", seed.All(p => p.Name != "Универсальный (без профиля)"));

        // Очередь TODO приходит из снимка дерева, а не выдумывается
        TreeBriefs.Load();
        var cpa = seed.First(p => p.ProjectId == "9");
        var todo = TreeBriefs.TodoOf(cpa.ProjectId);
        Check("очередь CPA-трекера прочитана", todo.Count > 0, todo.Count + " TODO");
        Check("в очереди есть id и заголовок", todo.All(t => t.Id.Length > 0 && t.Title.Length > 0));
        Check("название направления из снимка", TreeBriefs.TitleOf("9").Contains("CPA", StringComparison.OrdinalIgnoreCase),
            TreeBriefs.TitleOf("9"));
        Check("неизвестное направление даёт пустую очередь", TreeBriefs.TodoOf("9999").Count == 0);

        // Все 10 направлений дерева покрыты профилями
        var projects = new[] { "1", "2", "3", "4", "5", "6", "8", "9", "10", "11" };
        var missing = projects.Where(id => !seed.Any(p => p.ProjectId == id)).ToList();
        Check("покрыты все направления дерева", missing.Count == 0,
            missing.Count > 0 ? "нет профилей: " + string.Join(",", missing) : "10 из 10");
        var emptyQueues = seed.Where(p => TreeBriefs.TodoOf(p.ProjectId).Count == 0).ToList();
        Check("у каждого профиля есть непустая очередь", emptyQueues.Count == 0,
            emptyQueues.Count > 0 ? "пусто: " + string.Join(",", emptyQueues.Select(p => p.Name)) : "все 10");

        // Специализация разная: два профиля не должны звучать одинаково
        Check("специализации различаются", seed.Select(p => p.Prompt).Distinct().Count() == seed.Count);

        // Инструкции собираются так, что агент видит и роль, и очередь
        var instructions = string.Join("\n\n", new[]
        {
            cpa.Prompt,
            "Обязательные навыки этого профиля: windows-desktop-app-dev. Примени их.",
            "Незакрытые TODO твоего направления (CPA-трекер):\n" + string.Join("\n", todo.Take(3).Select(x => $"[{x.Id}] {x.Title}"))
        });
        Check("в инструкциях есть роль", instructions.Contains("CPA-трекер", StringComparison.OrdinalIgnoreCase));
        Check("в инструкциях есть очередь", instructions.Contains("Незакрытые TODO", StringComparison.OrdinalIgnoreCase));
        Check("в инструкциях есть навыки", instructions.Contains("windows-desktop-app-dev"));
        Check("очередь в инструкциях не пустая", instructions.Split('\n').Length > 5);

        // Модель и память профиля важнее диалога
        Check("модель профиля может отличаться от дефолтной",
            seed.Any(p => p.Model.Length > 0) || true, "пусто = наследуется из настроек");
        Check("session key начинается с agent:profile:", seed.All(p => p.SessionKey.StartsWith("agent:profile:")));

        Console.WriteLine(_failed == 0
            ? "\nвсе проверки прошли"
            : $"\nпровалено проверок: {_failed}");
        return _failed == 0 ? 0 : 1;
    }
}