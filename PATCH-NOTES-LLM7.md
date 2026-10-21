# PATCH-NOTES — пакет LLM7 (ai-job-search-by-llm7.zip)

**Дата:** 2026-10-21 · **Основание:** решение владельца «добавь в продуктовую
цепочку. и учти критику мистраля» · **Пререквизит:** LOG-CONTRACT.md
(logcontract-пакет от 2026-10-21; если утерян — файлы пакета самодостаточны)

## Состав zip

```
LLM7-INTEGRATION.md              — дизайн, роутинг, бюджетная математика, интеграция
PATCH-NOTES-LLM7.md              — этот файл
src/Services/ILogService.cs      — машина протокола (идентичен logcontract-пакету)
src/Services/Ai/AiChatModels.cs      — типы цепи
src/Services/Ai/IAiChatProvider.cs   — единый интерфейс провайдеров
src/Services/Ai/Llm7Provider.cs      — провайдер LLM7 + регистр бюджета
src/Services/Ai/LocalMatchScorer.cs  — локальная оценка соответствия
src/Services/Ai/ResponseCostCounter.cs — счётчик расхода [cost]
src/Services/Ai/TaskRoutingPolicy.cs   — роутинг стадий + фолбэк-цепь
src/Tests/Logging/LogServiceTests.cs   — 7 тестов машины протокола
src/Tests/Ai/Llm7ProviderTests.cs      — 7 тестов провайдера
src/Tests/Ai/RoutingScorerCostTests.cs — 7 тестов роутинга/скоринга/счётчика
```

## Порядок применения

1. Если logcontract-пакет применён — `src/Services/ILogService.cs` пропустить
   (файл идентичен). Если НЕ применён — положить его в `src/Services/`
   (контрактная машина протокола обязательна: всё ниже кричит через неё).
2. `src/Services/Ai/*.cs` → в `src/` рядом с существующими сервисами.
3. Тесты → в тестовый проект; `ILogService`-фикстуры переиспользуются
   (`LogServiceTests.TempLogDir()`).
4. Интеграция в цепь — LLM7-INTEGRATION.md §7: DI, обёртка существующих
   OpenRouter/OrcaRouter под `IAiChatProvider`, `ProbeModelsAsync()` на старте,
   стадия match → `LocalMatchScorer`.
5. Env: `AIJOB_LLM7_API_KEY` (пусто → анонимно с WARN; токен — dash.llm7.io),
   `AIJOB_LLM7_MODEL` (дефолт `DeepSeek-V4-Flash-0731`).

## Дельты для доков (внести владельцем)

- **PLAN.md §2** — строка: «Стадия match — без ИИ (локальный скоринг);
  письма — llm7 (free 100K ток/24ч); расход каждого отклика — строка [cost]
  (решение 2026-10-21 по критике Мистраля)».
- **PLAN.md §12.2 [Unreleased] — Added:** «LLM7 — третий ИИ-провайдер
  (OpenAI-совместимый, анонимный режим, локальный регистр бюджета,
  429→фолбэк); LocalMatchScorer заменил ИИ-оценку соответствия;
  ResponseCostCounter — фактический расход на отклик».
- **DECISIONS.md** — остаётся замороженным до WP-0; вставка после разморозки
  (решение владельца, основания — LLM7-INTEGRATION.md §1–§2).
- **README** — счётчик провайдеров цепи обновится генератором при релизе.

## Верификация (прогнано в песочнице 2026-10-21)

- `dotnet test` — **21/21** (SDK 10.0.402);
- живая проба `GET /v1/models` — каталог 70 моделей, turbo-тир прочитан;
- живой анонимный `chat/completions` — ответ получен, парсер совпал (~76 ток);
- аудит тишины §8 LLM7-INTEGRATION.md — чисто.

## Что пакет НЕ делает

- Не трогает существующие файлы OpenRouter/OrcaRouter — их адаптация под
  `IAiChatProvider` выполняется после репо-зипа (правило: не править вслепую).
- Не включает Aion Labs (решение по критике Мистраля: 20K ток/день ≈ +1 отклик).
- Не меняет потолок ~5–7 откликов/день из README — он пересчитывается
  фактическими числами счётчика после первого живого прогона.
