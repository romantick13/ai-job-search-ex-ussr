namespace AiJobSearchBy.Services;

// FLOW: seam для чтения ключей порталов из слоя секретов (config/local.json,
// %APPDATA%\ai-job-search-by\). Реализация — ConfigService; до неё —
// NullLocalSecrets,
// чтобы KeysCheckService и тесты жили без конфиг-слоя. Секреты НИКОГДА не
// попадают в репозиторий, логи и промпты (PRIVACY.md, secrets-guard CI).
public interface ILocalSecrets
{
    // Ключ портала по его Id (например «adzuna»); отсутствует → null.
    string? GetPortalKey(string portalId);
}

// Заглушка: ключей нет — все ключевые порталы честно получают no_key
// (старт без ключей = auth=none-порталы работают сразу).
public sealed class NullLocalSecrets : ILocalSecrets
{
    public string? GetPortalKey(string portalId) => null;
}
