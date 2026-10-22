using System.IO;
using System.Security.Cryptography;
using AiJobSearchBy.Services.Ai;

namespace AiJobSearchBy.Services;

// ============================================================================
// WP-U1 (требование владельца 2026-10-22): «первый запуск приложения —
// векторизует все PDF, которые мы создаём; сами файлы остаются на диске,
// в базе — векторные ссылки». WP-U2: вектор строит ЛОКАЛЬНЫЙ эмбеддер
// (Qwen3-Embedding-0.6B по умолчанию; Ollama/LM Studio/llama-server).
// Индексатор сканирует папку откликов, новые файлы (не в PdfDocuments)
// получает текст (PdfPig 0.1.16 — пин интеграции, THIRD-PARTY-NOTICES),
// строит вектор (если эмбеддер жив) и пишет строку индекса. Перезапуск
// по кнопке «⟳ PDF» в окне истории.
//
// WP-U2: протухание векторов — смена модели эмбеддера делает старые
// векторы несравнимыми (другое пространство). Строки PdfDocuments с чужой/
// пустой VectorModel и с извлечённым текстом пере-векторизуются ИЗ БАЗЫ
// (PDF не перечитываем) — индекс сходится к текущей модели.
//
// Политика «никаких тихих игноров» (LOG-CONTRACT.md): каждый нечитаемый PDF —
// LogWarn с причиной; сводка прогона — LogSuccess; живость рантайма решает
// самопробник LocalEmbeddingService (один WARN с решением), индексация
// продолжается без векторов.
//
// FLOW: старт приложения (фон) / кнопка «⟳ PDF» -> IndexNewPdfsAsync ->
//       ApplicationStore.SavePdfIndex.
// STATE: без состояния — повторный вызов индексирует только новые файлы.
// THREAD: вызовы — Task.Run; внутри последовательно (вежливость к диску).
// ============================================================================
public sealed class PdfIndexerService
{
    private readonly IApplicationStore _store;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogService _log;
    private readonly Func<string> _outboxDir;

    public PdfIndexerService(IApplicationStore store, IEmbeddingService embeddings,
        ILogService log, Func<string> outboxDir)
    {
        _store = store;
        _embeddings = embeddings;
        _log = log;
        _outboxDir = outboxDir;
    }

    // Возвращает число новых проиндексированных файлов.
    public async Task<int> IndexNewPdfsAsync(CancellationToken ct = default)
    {
        var dir = _outboxDir();
        if (!Directory.Exists(dir))
        {
            _log.LogWarn($"skip: [pdf] переиндексация не запускалась — папка откликов ещё не создана: {dir}; " +
                         "решение: папка появится при первой отправке отклика");
            return 0;
        }

        // WP-U2: кнопка «⟳ PDF» — явное действие юзера = новая попытка
        // самопробника (рантайм могли поднять/перезапустить с прошлого раза).
        (_embeddings as Ai.LocalEmbeddingService)?.ResetProbe();

        var indexed = _store.ListIndexedPdfs()
            .Select(p => p.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly);
        var fresh = files.Where(f => !indexed.Contains(f)).ToList();
        var done = 0;

        // Протухшие векторы переезжают и когда новых файлов нет (кнопка «⟳ PDF»).
        if (fresh.Count == 0)
        {
            _log.Log($"[pdf] переиндексация: новых PDF нет ({files.Length} в {dir})");
            await ReembedStaleVectorsAsync(ct).ConfigureAwait(false);
            return 0;
        }

        _log.Log($"[pdf] переиндексация: {fresh.Count} новых PDF из {files.Length} ({dir}), " +
                 $"векторизация: {(_embeddings.Enabled ? _embeddings.Model : "выключена — самопробник не прошёл (причина в протоколе)")}");
        foreach (var path in fresh)
        {
            ct.ThrowIfCancellationRequested();
            string? text;
            try
            {
                text = ExtractText(path);
            }
            catch (Exception ex)
            {
                // НИ-1: нечитаемый PDF — не тихий пропуск; в базу идёт без текста.
                _log.LogWarn($"skip: [pdf] текст не извлечён из {Path.GetFileName(path)} — " +
                             $"{ex.GetType().Name}: {ex.Message}; решение: индексирую файл без текста " +
                             "(вектор не строится)");
                text = null;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError($"[pdf] файл не читается: {path} — {ex.Message}; решение: пропуск, " +
                              "файл проиндексируется при следующем прогоне");
                continue;
            }

            var vector = _embeddings.Enabled && !string.IsNullOrWhiteSpace(text)
                ? await _embeddings.EmbedAsync(text!, ct).ConfigureAwait(false)
                : null;

            _store.SavePdfIndex(new PdfDocumentRecord
            {
                Id = Guid.NewGuid(),
                FilePath = path,
                FileHash = Convert.ToHexString(SHA256.HashData(bytes)),
                ExtractedText = text,
                VectorJson = vector is null ? null : Ai.VectorJson.To(vector),
                VectorModel = vector is null ? null : _embeddings.Model,
                IndexedAt = DateTimeOffset.Now,
            });
            done++;
            _log.Log($"[pdf] проиндексирован: {Path.GetFileName(path)} " +
                     $"(текст {text?.Length ?? 0} зн., вектор {(vector is null ? "—" : vector.Length.ToString())})");
        }

        _log.LogSuccess($"[pdf] переиндексация завершена: {done} новых PDF");
        await ReembedStaleVectorsAsync(ct).ConfigureAwait(false);
        return done;
    }

    // ------------------------------------------------------------------
    // WP-U2: протухание векторов. Векторы сравнимы только внутри одного
    // пространства — сменилась VectorModel, значит старые в базу больше не
    // годятся. Строки с текстом и чужой/пустой VectorModel пере-векторизуются
    // из ExtractedText (PDF не перечитываем); без текста — не трогаем (их
    // вектор невозможен, причина уже в протоколе файла). Кнопка «⟳ PDF»
    // повторяет попытку — сходится за конечное число прогонов.
    // ------------------------------------------------------------------
    private async Task ReembedStaleVectorsAsync(CancellationToken ct)
    {
        if (!_embeddings.Enabled)
        {
            return; // причина — WARN самопробника, повторять нечего
        }

        var stale = _store.ListIndexedPdfs()
            .Where(p => !string.IsNullOrWhiteSpace(p.ExtractedText)
                        && !string.Equals(p.VectorModel, _embeddings.Model, StringComparison.Ordinal))
            .ToList();
        if (stale.Count == 0)
        {
            return;
        }

        _log.Log($"[pdf] векторы под текущую модель {_embeddings.Model}: {stale.Count} шт. (пере-векторизация из базы)");
        var switched = 0;
        foreach (var row in stale)
        {
            ct.ThrowIfCancellationRequested();
            var vector = await _embeddings.EmbedAsync(row.ExtractedText!, ct).ConfigureAwait(false);
            if (vector is null)
            {
                continue; // причина — в протоколе, файл не теряется
            }
            _store.SavePdfIndex(row with
            {
                VectorJson = Ai.VectorJson.To(vector),
                VectorModel = _embeddings.Model,
            });
            switched++;
        }
        if (switched > 0)
        {
            _log.LogSuccess($"[pdf] векторы переведены на {_embeddings.Model}: {switched} шт.");
        }
    }

    // PdfPig: текст постранично; у наших PDF (QuestPDF) extraction чистый.
    public static string? ExtractText(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        var builder = new System.Text.StringBuilder();
        foreach (var page in document.GetPages())
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.Append(page.Text);
        }
        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
}
