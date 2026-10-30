using System.IO;
using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AiJobSearchBy.Services;

// ============================================================================
// Точечная заплатка
// ЧУЖОГО PDF — белый прямоугольник поверх слова + штамп замены тем же
// шрифтовым семейством, что и наш экспорт (Segoe UI; лицензии системных
// шрифтов Windows разрешают встраивание в документы пользователя).
// PdfPig (0.1.16, уже в csproj) даёт слова с координатами, PDFsharp 6.1.1
// (MIT) рисует поверх (PdfSharpCore 1.3.1 отклонён: тянет ImageSharp 1.0.0
// с NU1903, а последний ImageSharp 3.x тоже под advisory; у PDFsharp 6.2+
// runtime-лицензия — потому пин 6.1.1, последняя чистая MIT). iText отклонён
// (AGPL, несовместим с MIT).

// FLOW: MainWindow («Заплатка PDF») -> PdfOverlayWindow -> выбор файла ->
// FindAsync (слова-кандидаты со страниц) -> ApplyAsync (плашка + штамп,
// атомарная подмена temp-копией) -> обновление строки PdfDocuments
// (текст/хеш; вектор сбрасывается — «⟳ PDF» перестроит из базы).
// STATE: без состояния; каждый вызов открывает файл заново.
// THREAD: PDFsharp и кеш резолвера не потокобезопасны — операции под
// статическим _pdfGate; resolver ставится в процесс ровно один раз.
// ============================================================================

// Вхождение слова в чужом PDF: страница 1-based, X/Y/Width/Height — PDF-пространство
// (начало координат — левый НИЖНИЙ угол страницы, единицы — пункты).
public sealed record PdfOverlayHit(int PageNumber, string Word, double X, double Y,
    double Width, double Height);

// Результат сканирования: сколько вхождений, список кандидатов, страниц в файле.
public sealed record PdfOverlayScan(int Hits, IReadOnlyList<PdfOverlayHit> Items, int PageCount);

// Результат заплатки: сколько слов закрашено/перенапечатано, итоговый путь.
public sealed record PdfOverlayResult(int Applied, string Path);

public interface IPdfOverlay
{
    // Слова, содержащие find (подстрока внутри токена; регистр — по флажку).
    Task<PdfOverlayScan> FindAsync(string path, string find, bool matchCase,
        CancellationToken ct = default);

    // Белая плашка поверх каждого вхождения + штамп replace (пусто/null —
    // только закрасить). Сохраняет файл атомарно; обновляет индекс PdfDocuments.
    Task<PdfOverlayResult> ApplyAsync(string path, IReadOnlyList<PdfOverlayHit> hits,
        string? replace, CancellationToken ct = default);
}

public sealed class PdfOverlayService : IPdfOverlay
{
    // Плашка выходит за bbox слова на 1.5 pt с каждой стороны — старые глифы
    // не должны выглядывать из-под белого.
    private const double Pad = 1.5;

    private readonly IApplicationStore? _store;
    private readonly ILogService _log;
    private static readonly object _pdfGate = new();
    private static readonly object _resolverGate = new();
    private static bool _resolverInstalled;

    public PdfOverlayService(IApplicationStore? store, ILogService log)
    {
        _store = store;
        _log = log;
        EnsureResolver();
    }


    // Поиск: PdfPig даёт слова-токены; ищем подстроку ВНУТРИ токена
    // («Ромашка» найдётся и в «Ромашка,» — заплатка закрывает токен целиком).

    public Task<PdfOverlayScan> FindAsync(string path, string find, bool matchCase,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(find))
        {
            _log.LogWarn("skip: [pdf-overlay] поиск не выполнен — причина: пустая строка поиска; " +
                         "решение: ввести слово или его часть");
            return Task.FromResult(new PdfOverlayScan(0, [], 0));
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = new List<PdfOverlayHit>();
        int pageCount;
        lock (_pdfGate)
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            pageCount = document.NumberOfPages;
            var pageNumber = 0;
            foreach (var page in document.GetPages())
            {
                pageNumber++; // 1-based, как в списке страниц у просмотрщиков
                foreach (var word in page.GetWords())
                {
                    if (!word.Text.Contains(find, comparison))
                    {
                        continue;
                    }
                    var box = word.BoundingBox;
                    hits.Add(new PdfOverlayHit(pageNumber, word.Text,
                        box.Left, box.Bottom, box.Width, box.Height));
                }
            }
        }

        _log.Log($"[pdf-overlay] поиск «{find}» в {Path.GetFileName(path)}: {hits.Count} вх. на {pageCount} стр.");
        return Task.FromResult(new PdfOverlayScan(hits.Count, hits, pageCount));
    }


    // Заплатка: белая плашка поверх каждого вхождения + штамп замены.
    // Размер штампа: от высоты слова (чернила ~1.15 em), сжатие по ширине,
    // если замена длиннее оригинала. Порядок: файл -> индекс; сбой индекса
    // файл не откатывает (факт — в протоколе, «⟳ PDF» обновит текст позже).

    public Task<PdfOverlayResult> ApplyAsync(string path, IReadOnlyList<PdfOverlayHit> hits,
        string? replace, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (hits.Count == 0)
        {
            _log.LogWarn("skip: [pdf-overlay] заплатка не выполнена — причина: список вхождений пуст; " +
                         "решение: сначала «Найти» — показать, что именно будет закрыто");
            return Task.FromResult(new PdfOverlayResult(0, path));
        }

        EnsureResolver();
        var tempPath = BuildTempPath(path);
        int applied = 0;
        try
        {
            lock (_pdfGate)
            {
                using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                applied = StampHits(doc, hits, replace);
                doc.Save(tempPath);
            }

            // Атомарная подмена: поверх существующего — Replace (тот же файл,
            // права сохраняются), нового — Move. Файл в просмотрщике (занят)
            // даст IOException — его ловит вызывающее окно с решением.
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        _log.LogSuccess($"[pdf-overlay] заплачено {applied} × «{(hits.Count > 0 ? hits[0].Word : "?")}» " +
                        $"-> «{replace ?? "(закрашено)"}»: {path}");
        RefreshIndex(path, applied);
        return Task.FromResult(new PdfOverlayResult(applied, path));
    }

    // Плашка + штамп по страницам; возвращает число обработанных вхождений.
    private int StampHits(PdfDocument doc, IReadOnlyList<PdfOverlayHit> hits, string? replace)
    {
        var applied = 0;
        var byPage = hits.GroupBy(h => h.PageNumber);
        foreach (var group in byPage)
        {
            var page = doc.Pages[group.Key - 1];
            var pageW = page.Width.Point;
            var pageH = page.Height.Point;
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            foreach (var hit in group)
            {
                // PDF-пространство (низ-лево) -> XGraphics (верх-лево): y = H - Y.
                var x = Math.Max(0, hit.X - Pad);
                var y = Math.Max(0, pageH - hit.Y - hit.Height - Pad);
                var w = Math.Min(hit.Width + Pad * 2, pageW - x);
                var h = Math.Min(hit.Height + Pad * 2, pageH - y);
                var rect = new XRect(x, y, w, h);
                gfx.DrawRectangle(XBrushes.White, rect);
                applied++; // плашка легла — вхождение обработано (и для закраски)

                if (string.IsNullOrEmpty(replace))
                {
                    continue; // замена пуста = удалить слово (только закрасить)
                }

                // Высота чернил слова ~1.15 em; сжатие по ширине, если длиннее.
                var sizeOrig = hit.Height / 1.15;
                var probe = new XFont("Segoe UI", sizeOrig, XFontStyleEx.Regular,
                    new XPdfFontOptions(PdfFontEncoding.Unicode));
                var measured = gfx.MeasureString(replace, probe);
                var available = hit.Width + Pad * 2 - 2;
                var size = sizeOrig;
                if (measured.Width > available && measured.Width > 0)
                {
                    size = Math.Max(3, sizeOrig * available / measured.Width);
                    if (size < sizeOrig - 0.2)
                    {
                        _log.LogWarn($"[pdf-overlay] замена длиннее оригинала на стр. {hit.PageNumber} " +
                                     $"— шрифт ужат до {size:0.#} pt; решение: сократить замену или " +
                                     "принять мелкий штамп");
                    }
                }

                var font = new XFont("Segoe UI", size, XFontStyleEx.Regular,
                    new XPdfFontOptions(PdfFontEncoding.Unicode));
                gfx.DrawString(replace, font, XBrushes.Black, rect, XStringFormats.CenterLeft);
            }
        }
        return applied;
    }


    // Индекс PdfDocuments: после подмены файла строка протухла (хеш/текст/
    // вектор). Обновляем текст и хеш из нового файла, вектор сбрасываем —
    // Правило пере-векторизации из базы: строку подхватит кнопка «⟳ PDF».

    private void RefreshIndex(string path, int applied)
    {
        if (_store is null)
        {
            _log.Log("[pdf-overlay] хранилище не подключено (дизайнер/тесты без контейнера) — " +
                     "строка PdfDocuments не обновлялась");
            return;
        }
        try
        {
            var row = _store.ListIndexedPdfs().FirstOrDefault(p =>
                string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                _log.Log($"[pdf-overlay] {Path.GetFileName(path)} не индексировался — строку " +
                         "PdfDocuments не трогаю (файл вне папки откликов или ещё не индексировался)");
                return;
            }

            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var text = ExtractText(path);
            _store.SavePdfIndex(row with
            {
                FileHash = hash,
                ExtractedText = text,
                VectorJson = null,
                VectorModel = null,
                IndexedAt = DateTimeOffset.Now,
            });
            _log.Log($"[pdf-overlay] индекс обновлён ({applied} вх., текст {text?.Length ?? 0} зн.): " +
                     "вектор сброшен — кнопка «⟳ PDF» перестроит его из базы");
        }
        catch (Exception ex)
        {
            // Файл уже заплачен — откатывать нечего; сбой индекса не теряем.
            _log.LogError($"[pdf-overlay] индекс не обновлён для {Path.GetFileName(path)} — " +
                          $"{ex.GetType().Name}: {ex.Message}; решение: нажать «⟳ PDF» — " +
                          "строка переиндексируется как новая при смене хеша вручную");
        }
    }

    // Текст постранично (PdfPig), как в PdfIndexerService.ExtractText.
    private static string? ExtractText(string path)
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

    // Резолвер шрифтов ставится в процесс РОВНО один раз (повторная установка
    // кидает исключение); идемпотентно — как лицензия QuestPDF в PdfExportService.
    private void EnsureResolver()
    {
        if (_resolverInstalled)
        {
            return;
        }
        lock (_resolverGate)
        {
            if (_resolverInstalled)
            {
                return;
            }
            try
            {
                GlobalFontSettings.FontResolver = new PdfOverlayFontResolver();
            }
            catch (Exception ex)
            {
                _log.LogWarn($"skip: [pdf-overlay] свой резолвер шрифтов не установлен — причина: " +
                             $"{ex.Message}; решение: используется уже установленный в процесс");
            }
            _resolverInstalled = true;
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


// Шрифты для штампа: TTF с диска, встраиваются в PDF (Unicode). Порядок —
// семейство нашего экспорта (Segoe UI), затем Arial/Calibri/Tahoma; на Linux
// (тесты) — DejaVu Sans. Стиль (болд/италик) выбирается из соответствующего
// слота; faceName = полный путь к TTF. PDFsharp 6.1.1: резолвер = два метода
// (ResolveTypeface + GetFont), ставится в процесс один раз.

public sealed class PdfOverlayFontResolver : IFontResolver
{
    private static readonly string[] RegularCandidates =
    [
        "C:\\Windows\\Fonts\\segoeui.ttf",
        "C:\\Windows\\Fonts\\arial.ttf",
        "C:\\Windows\\Fonts\\calibri.ttf",
        "C:\\Windows\\Fonts\\tahoma.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    ];
    private static readonly string[] BoldCandidates =
    [
        "C:\\Windows\\Fonts\\segoeuib.ttf",
        "C:\\Windows\\Fonts\\arialbd.ttf",
        "C:\\Windows\\Fonts\\calibrib.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    ];
    private static readonly string[] ItalicCandidates =
    [
        "C:\\Windows\\Fonts\\segoeuii.ttf",
        "C:\\Windows\\Fonts\\ariali.ttf",
        "C:\\Windows\\Fonts\\calibrii.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Oblique.ttf",
    ];
    private static readonly string[] BoldItalicCandidates =
    [
        "C:\\Windows\\Fonts\\segoeuiz.ttf",
        "C:\\Windows\\Fonts\\arialbi.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-BoldOblique.ttf",
    ];

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _cache = new();

    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var slot = bold
            ? (italic ? BoldItalicCandidates : BoldCandidates)
            : (italic ? ItalicCandidates : RegularCandidates);
        foreach (var candidate in slot)
        {
            if (File.Exists(candidate))
            {
                return new FontResolverInfo(candidate);
            }
        }
        throw new InvalidOperationException(
            "не найден ни один TTF-шрифт для штампа (Segoe UI/Arial/Calibri/Tahoma/DejaVu); " +
            "решение: проверить, что в системе есть хотя бы один из этих шрифтов");
    }

    public byte[] GetFont(string faceName) =>
        _cache.GetOrAdd(faceName, static p => File.ReadAllBytes(p));
}
