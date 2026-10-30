using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AiJobSearchBy.Services;

// ============================================================================
// Точечная правка DOCX нативно, без внешних редакторов. Сценарий —
// «одно-два слова/запятая»: меняем только текстовые ноды (w:t),
// форматирование файла не трогаем.

// DocumentFormat.OpenXml 3.2.0 (MIT) — пин THIRD-PARTY-NOTICES.md §6; пакет
// офлайн, внешних процессов не запускает (контракт сервиса).

// Правка переживает «рваные» run-ы: Word режет текст на произвольные куски
// («привет» может лежать как «при»+«вет»), поиск и замена работают по полному
// тексту абзаца с картой смещений в узлы. Правка подменяемого куска достаётся
// первому затронутому run-у — его форматирование и остаётся на месте.

// Ограничения (осознанные): вхождение не ищется через границу абзаца
// и через разрывы (tab/перевод строки внутри абзаца — барьеры); колонтитулы
// правятся, сноски — нет; замена не может содержать перевод строки/табуляцию.

// FLOW: DocxEditWindow (модалка поверх деталей отклика) -> ReplaceAsync ->
// temp-копия -> правка -> атомарная подмена файла. ReadTextAsync —
// превью и пересчёт вхождений.
// STATE: без состояния — потокобезопасен.
// THREAD: файловый IO синхронный внутри Task; из окна зовётся через Task.Run.
// ============================================================================
public interface IDocxTextEditor
{
    // Абзацы документа (тело, включая таблицы) — превью и подсчёт вхождений.
    Task<DocxDocumentText> ReadTextAsync(string path, CancellationToken ct = default);

    // Точечная замена с сохранением форматирования; replace null/пусто = удалить.
    // Атомарно: файл подменяется только после успешной правки temp-копии.
    Task<DocxReplaceResult> ReplaceAsync(string path, string find, string? replace,
        bool matchCase, CancellationToken ct = default);
}

public sealed record DocxDocumentText(IReadOnlyList<string> Paragraphs);

public sealed record DocxReplaceResult(int Replacements, string Path);

public sealed class DocxTextEditService : IDocxTextEditor
{
    private readonly ILogService _log;

    public DocxTextEditService(ILogService log)
    {
        _log = log;
    }

    public Task<DocxDocumentText> ReadTextAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        var paragraphs = body is null
            ? new List<string>()
            : body.Descendants<Paragraph>()
                .Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text ?? string.Empty)))
                .ToList();
        return Task.FromResult(new DocxDocumentText(paragraphs));
    }

    public Task<DocxReplaceResult> ReplaceAsync(string path, string find, string? replace,
        bool matchCase, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Контракт «никаких тихих игноров»: осознанные пропуски — с причиной и решением.
        if (string.IsNullOrEmpty(find))
        {
            _log.LogWarn("skip: [docx] замена не выполнена — причина: пустая строка поиска; " +
                         "решение: ввести точный текст, который нужно поправить");
            return Task.FromResult(new DocxReplaceResult(0, path));
        }
        if (replace is not null && replace.IndexOfAny(['\n', '\r', '\t']) >= 0)
        {
            _log.LogWarn("skip: [docx] замена не выполнена — причина: замена содержит " +
                         "перевод строки или табуляцию; решение: править в один приём без " +
                         "разрывов либо двумя заменами подряд");
            return Task.FromResult(new DocxReplaceResult(0, path));
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var temp = BuildTempPath(path);

        try
        {
            File.Copy(path, temp, overwrite: true);

            int count;
            using (var doc = WordprocessingDocument.Open(temp, true))
            {
                var main = doc.MainDocumentPart
                    ?? throw new InvalidOperationException(
                        "в файле нет основной части документа — это не .docx " +
                        "(старый .doc/.rtf под другим расширением?)");
                count = ReplaceEverywhere(main, find, replace ?? string.Empty, comparison);
                main.Document?.Save();
            }

            if (count == 0)
            {
                // Оригинал не трогаем: время файла и байты живут, temp убирает finally.
                _log.Log($"[docx] вхождений не найдено: «{find}» ({Path.GetFileName(path)})");
                return Task.FromResult(new DocxReplaceResult(0, path));
            }

            // Атомарная подмена на том же томе; занятый Word-ом файл даст IOException —
            // наружу, окно кричит в протокол с решением.
            File.Move(temp, path, overwrite: true);
            _log.LogSuccess($"[docx] заменено {count} × «{find}»: {Path.GetFileName(path)}");
            return Task.FromResult(new DocxReplaceResult(count, path));
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
            // temp-мусор в каталоге документа не критичен; сбой подмены уже уйдёт наружу.
            }
        }
    }

    // Тело документа + колонтитулы (сноски — за рамками сервиса, их в резюме нет).
    private static int ReplaceEverywhere(MainDocumentPart main, string find, string replace,
        StringComparison comparison)
    {
        var roots = new List<OpenXmlElement>();
        if (main.Document?.Body is { } body)
        {
            roots.Add(body);
        }
        foreach (var header in main.HeaderParts)
        {
            roots.Add(header.Header);
        }
        foreach (var footer in main.FooterParts)
        {
            roots.Add(footer.Footer);
        }
        return roots.Sum(root => ReplaceInRoot(root, find, replace, comparison));
    }

    private static int ReplaceInRoot(OpenXmlElement root, string find, string replace,
        StringComparison comparison)
    {
        var total = 0;
        foreach (var paragraph in root.Descendants<Paragraph>().ToList())
        {
            total += ReplaceInParagraph(paragraph, find, replace, comparison);
        }
        return total;
    }

    // Замена в одном абзаце: полный текст абзаца + карта «символ -> (узел, смещение)»,
    // вхождения правятся с конца, чтобы ранние смещения не плыли. Форматирование
    // run-ов не трогаем: правится только содержимое w:t, узлы не пересоздаются.
    private static int ReplaceInParagraph(Paragraph paragraph, string find, string replace,
        StringComparison comparison)
    {
        var texts = new List<Text>();
        var nodeOf = new List<int>();  // индекс t-узла для каждого символа; -1 — барьер
        var charOf = new List<int>();  // смещение внутри t-узла; -1 — барьер
        var full = new StringBuilder();

        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text text:
                    var nodeIndex = texts.Count;
                    texts.Add(text);
                    var value = text.Text ?? string.Empty;
                    for (var i = 0; i < value.Length; i++)
                    {
                        nodeOf.Add(nodeIndex);
                        charOf.Add(i);
                        full.Append(value[i]);
                    }
                    break;
                case TabChar:
                    nodeOf.Add(-1);
                    charOf.Add(-1);
                    full.Append('\t');
                    break;
                case Break or CarriageReturn:
                    nodeOf.Add(-1);
                    charOf.Add(-1);
                    full.Append('\n');
                    break;
            }
        }

        if (texts.Count == 0)
        {
            return 0;
        }

        // Непересекающиеся вхождения слева направо, применение — с конца.
        var hay = full.ToString();
        var hits = new List<(int Start, int End)>();
        var at = hay.IndexOf(find, comparison);
        while (at >= 0)
        {
            hits.Add((at, at + find.Length));
            at = hay.IndexOf(find, at + find.Length, comparison);
        }

        var count = 0;
        for (var h = hits.Count - 1; h >= 0; h--)
        {
            var (start, end) = hits[h];
            if (ContainsBarrier(nodeOf, start, end))
            {
                continue; // вхождение через tab/разрыв не правим — это уже не «одно-два слова»
            }
            ApplyMatch(texts, nodeOf, charOf, start, end, replace);
            count++;
        }
        return count;
    }

    private static bool ContainsBarrier(List<int> nodeOf, int start, int end)
    {
        for (var p = start; p < end; p++)
        {
            if (nodeOf[p] < 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void ApplyMatch(List<Text> texts, List<int> nodeOf, List<int> charOf,
        int start, int end, string replace)
    {
        // Затронутые узлы со смещениями внутри каждого (символы узла в карте идут подряд).
        var affected = new List<(int Node, int From, int ToExclusive)>();
        var currentNode = -1;
        var from = 0;
        var to = 0;
        for (var p = start; p < end; p++)
        {
            var node = nodeOf[p];
            if (node != currentNode)
            {
                if (currentNode >= 0)
                {
                    affected.Add((currentNode, from, to));
                }
                currentNode = node;
                from = charOf[p];
                to = charOf[p] + 1;
            }
            else
            {
                to = charOf[p] + 1;
            }
        }
        if (currentNode >= 0)
        {
            affected.Add((currentNode, from, to));
        }

        for (var a = 0; a < affected.Count; a++)
        {
            var (node, localFrom, localTo) = affected[a];
            var text = texts[node];
            var original = text.Text ?? string.Empty;
            var prefix = original[..localFrom];
            var suffix = original[localTo..];
            // Замена живёт в первом затронутом узле (его форматирование побеждает),
            // остальные узлы теряют покрытые вхождением куски.
            var updated = a == 0 ? prefix + replace + suffix : prefix + suffix;
            if (updated != original)
            {
                text.Text = updated;
                // Пробелы на краях w:t без xml:space="preserve" Word схлопывает.
                text.Space = SpaceProcessingModeValues.Preserve;
            }
        }
    }

    private static string BuildTempPath(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        return Path.Combine(
            string.IsNullOrEmpty(dir) ? "." : dir,
            $".{name}.{Guid.NewGuid():N}.tmp");
    }
}
