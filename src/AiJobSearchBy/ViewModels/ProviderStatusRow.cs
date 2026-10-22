namespace AiJobSearchBy.ViewModels;

// WP-U0 (морда вчерне): immutable-строка карточки провайдера для дашборда.
// Собирается ОДИН раз при старте VM из конкретных типов провайдеров
// (IAiChatProvider сознательно не расширяли — сурфейс только у конкретных
// типов, см. Model/IsKeyMissing/IsLocal/IsAnonymous). Живых изменений не
// хранит: перезапуск приложения = новая карточка — для вчерне достаточно,
// живой статус дальней дороги (WP-U) придёт с probe-событиями.
public sealed class ProviderStatusRow
{
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public bool KeyPresent { get; init; }
    public bool InChain { get; init; }
    public string KeyNote { get; init; } = "";

    public static ProviderStatusRow From(Services.Ai.IAiChatProvider provider) => provider switch
    {
        Services.Ai.OpenAiCompatibleProvider oc => new ProviderStatusRow
        {
            Name = oc.Name,
            Model = oc.Model,
            KeyPresent = !oc.IsKeyMissing,
            InChain = !oc.IsKeyMissing,
            KeyNote = oc.IsKeyMissing
                ? "ключ НЕТ — вне цепи"
                : oc.IsLocal ? "локальный, без ключа" : "ключ: токен",
        },
        Services.Ai.Llm7Provider l7 => new ProviderStatusRow
        {
            Name = l7.Name,
            Model = l7.Model,
            KeyPresent = !l7.IsAnonymous,
            InChain = true, // llm7 работает и анонимно — из цепи не выпадает
            KeyNote = l7.IsAnonymous ? "анонимно (лимиты ниже)" : "ключ: токен",
        },
        _ => new ProviderStatusRow
        {
            Name = provider.Name,
            Model = "—",
            KeyPresent = true,
            InChain = true,
            KeyNote = "готов",
        },
    };
}
