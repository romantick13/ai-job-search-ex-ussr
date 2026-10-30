using System.IO;
using System.Security.Cryptography;

namespace AiJobSearchExUssr.Services;

// ============================================================================
// Индексатор PDF-папки: сканирует папку откликов, новые файлы (не в
// PdfDocuments) получает текст (PdfPig 0.1.16 — пин интеграции,
// THIRD-PARTY-NOTICES) и пишет строку индекса. Перезапуск по кнопке
// «⟳ PDF» в окне истории.
// r21: векторизация вырезана (решение владельца — векторы ничего не
// потребляли, функции «похожие отклики» не существовало). Индекс остался:
// извлечённый текст PDF в базе; столбцы VectorJson/VectorModel в таблице
// остались от прежних баз и пишутся пустыми.

// Политика «никаких тихих игноров»: каждый нечитаемый PDF —
// LogWarn с причиной; сводка прогона — LogSuccess.

// FLOW: старт приложения (фон) / кнопка «⟳ PDF» -> IndexNewPdfsAsync ->
// ApplicationStore.SavePdfIndex.
// STATE: без состояния — повторный вызов индексирует только новые файлы.
// THREAD: вызовы — Task.Run; внутри последовательно (вежливость к диску).
// ============================================================================
public sealed class PdfIndexerService
{
    private readonly IApplicationStore _store;
    private readonly ILogService _log;
    private readonly Func<string> _outboxDir;

    public PdfIndexerService(IApplicationStore store, ILogService log, Func<string> outboxDir)
    {
        _store = store;
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

        var indexed = _store.ListIndexedPdfs()
            .Select(p => p.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly);
        var fresh = files.Where(f => !indexed.Contains(f)).ToList();
        if (fresh.Count == 0)
        {
            _log.Log($"[pdf] переиндексация: новых PDF нет ({files.Length} в {dir})");
            return 0;
        }

        _log.Log($"[pdf] переиндексация: {fresh.Count} новых PDF из {files.Length} ({dir})");
        var done = 0;
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
                // нечитаемый PDF — не тихий пропуск; в базу идёт без текста.
                _log.LogWarn($"skip: [pdf] текст не извлечён из {Path.GetFileName(path)} — " +
                             $"{ex.GetType().Name}: {ex.Message}; решение: индексирую файл без текста");
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

            _store.SavePdfIndex(new PdfDocumentRecord
            {
                Id = Guid.NewGuid(),
                FilePath = path,
                FileHash = Convert.ToHexString(SHA256.HashData(bytes)),
                ExtractedText = text,
                VectorJson = null, // r21: векторизация вырезана — столбцы остались пустыми
                VectorModel = null,
                IndexedAt = DateTimeOffset.Now,
            });
            done++;
            _log.Log($"[pdf] проиндексирован: {Path.GetFileName(path)} " +
                     $"(текст {text?.Length ?? 0} зн.)");
        }

        _log.LogSuccess($"[pdf] переиндексация завершена: {done} новых PDF");
        return done;
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
