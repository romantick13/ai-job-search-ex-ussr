using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// ChainProbe — живая проба одного звена ИИ-цепи для «Проверить всех».
// Принцип: НОЛЬ сожжённых токенов —
// проверяется GET {base}models (каталог), а не chat/completions: ключ
// валидируется, живость адреса подтверждается, бесплатные промпты экономятся.

// Ключ передаётся СВЕЖИМ (лестница env -> local.json / Chain.Custom): после любых
// правок в настройках (новый ключ, сгенерированный апи) проверка
// работает без перезапуска приложения.

// Полинг запрещён (architecture.md §3.2): вызывается только кнопкой UI.
// Живой кейс: openrouter молчал по 15 с и падал с первой
// попытки — «сервер молчит». Для него attempts=5 с паузой 2 с; каждая попытка
// строкой в протокол («попытка 1/5 …»), любая удачная = звено ОК, все мимо —
// честный вердикт «не прошло за 5 попыток». Остальные звенья: 1 попытка (как было).
// STATE: без состояния — один общий HttpClient (таймаут 15 с), потокобезопасен.
// ============================================================================
public sealed record ChainProbeOutcome(bool Ok, string Reason, TimeSpan Duration);

public static class ChainProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // attempts — сколько раз долбить звено (openrouter: 5);
    // pauseBetween — пауза между неудачами (по умолчанию 2 с);
    // onAttempt — куда писать каждую попытку (окно + протокол + сессионный лог).
    // Дефолт attempts=1 — прежнее поведение остальных звеньев, без лишних строк.
    // FLOW: попытка -> ОК? вернуть (с пометкой «попытка N из M», если не первая)
    // -> повтор бессмыслен (ключ/баланс/адрес/4xx)? вернуть как есть
    // -> иначе пауза и снова; попытки кончились -> «не прошло за N попыток».
    public static async Task<ChainProbeOutcome> RunAsync(
        string baseUrl, bool requiresKey, string? apiKey, CancellationToken ct = default,
        int attempts = 1, TimeSpan? pauseBetween = null, Action<string>? onAttempt = null)
    {
        attempts = Math.Max(1, attempts);
        var pause = pauseBetween ?? TimeSpan.FromSeconds(2);
        var total = TimeSpan.Zero;

        for (var n = 1; ; n++)
        {
            if (attempts > 1)
                onAttempt?.Invoke($"попытка {n}/{attempts} …");

            var (outcome, retryable) = await ProbeOnceAsync(baseUrl, requiresKey, apiKey, ct)
                .ConfigureAwait(false);
            total += outcome.Duration;

            if (outcome.Ok)
            {
                return attempts > 1 && n > 1
                    ? outcome with { Reason = $"{outcome.Reason} (попытка {n} из {attempts})" }
                    : outcome;
            }

            if (n >= attempts || !retryable)
            {
                return attempts > 1 && n > 1
                    ? new ChainProbeOutcome(false,
                        $"не прошло за {n} попыток; последняя причина: {outcome.Reason}", total)
                    : outcome;
            }

            onAttempt?.Invoke($"попытка {n}/{attempts} не прошла — {outcome.Reason}; " +
                              $"пауза {pause.TotalSeconds:0} с и пробую ещё");
            try
            {
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return outcome; // юзер отменил проверку — отдаём последний итог
            }
        }
    }

    // FLOW: одна попытка: baseUrl (+requiresKey+apiKey) -> GET {base}models -> 2xx = ок;
    // 401/403 = ключ отклонён; 402 = нет баланса; 429 = лимит;
    // сеть/таймаут = не отвечает. Причина — человекочитаемая, в протокол.
    // retryable: true = сбой переходящий (таймаут/сеть/5xx) — повтор имеет
    // смысл; false = приговор (ключ, баланс, адрес) — повтор бессмыслен.
    private static async Task<(ChainProbeOutcome Outcome, bool Retryable)> ProbeOnceAsync(
        string baseUrl, bool requiresKey, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (new ChainProbeOutcome(false, "адрес звена не задан — впишите в настройках", TimeSpan.Zero), false);
        }
        if (requiresKey && string.IsNullOrWhiteSpace(apiKey))
        {
            return (new ChainProbeOutcome(
                false, "ключ отсутствует — введите в настройках (проверка берёт его сразу, без перезапуска)", TimeSpan.Zero), false);
        }

        var url = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl + "models" : baseUrl + "/models";
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            sw.Stop();
            var code = (int)resp.StatusCode;
            var secs = $"{sw.Elapsed.TotalSeconds:0.#} с";
            return code switch
            {
                >= 200 and < 300 => (new ChainProbeOutcome(true, $"HTTP {code} — каталог моделей живой ({secs})", sw.Elapsed), false),
                401 => (new ChainProbeOutcome(false, $"HTTP 401 — ключ отклонён провайдером ({secs})", sw.Elapsed), false),
                // 403 на GET models — это доступ к КАТАЛОГУ (модель-специфичных
                // отказов тут не бывает): ключ/тариф/регион. r22h: не «ключ
                // отклонён» однозначно — провайдер может резать free-доступ политикой.
                403 => (new ChainProbeOutcome(false, $"HTTP 403 — доступ запрещён (ключ/тариф/регион — см. политику провайдера) ({secs})", sw.Elapsed), false),
                402 => (new ChainProbeOutcome(false, $"HTTP 402 — нет баланса/квоты, free-ключ не активирован ({secs})", sw.Elapsed), false),
                429 => (new ChainProbeOutcome(false, $"HTTP 429 — лимит запросов исчерпан, попробуйте позже ({secs})", sw.Elapsed), false),
                404 => (new ChainProbeOutcome(false, $"HTTP 404 — нет /models по адресу, проверьте BaseUrl ({secs})", sw.Elapsed), false),
                >= 500 and < 600 => (new ChainProbeOutcome(false, $"HTTP {code} — провайдер временно не работает ({secs})", sw.Elapsed), true),
                _ => (new ChainProbeOutcome(false, $"HTTP {code} — провайдер ответил ошибкой ({secs})", sw.Elapsed), false),
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return (new ChainProbeOutcome(false, "таймаут 15 с — сервер молчит (VPN/прокси/провайдер лежит?)", sw.Elapsed), true);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            sw.Stop();
            return (new ChainProbeOutcome(false, $"сервер не отвечает: {ex.Message}", sw.Elapsed), true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (new ChainProbeOutcome(false, $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed), false);
        }
    }
}
