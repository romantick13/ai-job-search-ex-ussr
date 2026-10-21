namespace AiJobSearchBy.Services;

// FLOW: seam для чтения ключей порталов из слоя секретов (config/local.json,
// %APPDATA%\ai-job-search-by\ — RFC-006 патч 05). Реализация придёт с
// ConfigService (RFC-004, порт из TechDocTrans); WP-P0 — NullLocalSecrets,
// чтобы KeysCheckService и тесты жили без конфиг-слоя. Секреты НИКОГДА не
// попадают в репозиторий, логи и промпты (PRIVACY.md, secrets-guard CI).
public interface ILocalSecrets
{
    // Ключ портала по его Id (например «adzuna»); отсутствует → null.
    string? GetPortalKey(string portalId);
}

// WP-P0-заглушка: ключей нет — все ключевые порталы честно получают no_key
// (канон §3.3: старт без ключей = auth=none-порталы работают сразу).
public sealed class NullLocalSecrets : ILocalSecrets
{
    public string? GetPortalKey(string portalId) => null;
}
