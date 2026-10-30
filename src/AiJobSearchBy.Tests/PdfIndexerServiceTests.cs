using AiJobSearchBy.Services;
using AiJobSearchBy.Tests;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: PdfIndexerService — первый запуск индексирует все PDF, которые мы
// создаём (требование к переиндексации). Новые файлы -> текст (PdfPig) ->
// PdfDocuments; повторный прогон не дублирует.
// r21: векторизация вырезана — тесты векторов (пере-векторизация чужих
// векторов, векторная ссылка) сняты; индекс текста остался и проверяется.
// THREAD: без сети — эмбеддера больше нет.
public class PdfIndexerServiceTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();
    private readonly string _outbox;
    private readonly ApplicationStore _store;
    private readonly SilentLog _log = new();

    public PdfIndexerServiceTests()
    {
        _outbox = Path.Combine(_dir, "outbox");
        Directory.CreateDirectory(_outbox);
        _store = new ApplicationStore(Path.Combine(_dir, "test.db"), new LogService(_dir, echoToConsole: false));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private PdfIndexerService MakeIndexer()
        => new(_store, _log, () => _outbox);

    private async Task<string> CreatePdfAsync(string employer)
    {
        var exporter = new PdfExportService(new LogService(_dir, echoToConsole: false));
        return await exporter.SaveResumePdfAsync(employer, "Senior Developer",
            "Текст резюме для индексации", DateTimeOffset.Now, _outbox);
    }

    [Fact]
    public async Task IndexNewPdfs_ExtractsText_AndWritesRow()
    {
        await CreatePdfAsync("ООО Ромашка");

        var done = await MakeIndexer().IndexNewPdfsAsync();

        Assert.Equal(1, done);
        var row = Assert.Single(_store.ListIndexedPdfs());
        Assert.Equal("ООО Ромашка", Path.GetFileNameWithoutExtension(row.FilePath).Split('_')[0]);
        Assert.False(string.IsNullOrWhiteSpace(row.ExtractedText));
        Assert.Contains("Текст резюме", row.ExtractedText);
        // r21: векторизация вырезана — векторные столбцы остаются пустыми.
        Assert.Null(row.VectorJson);
        Assert.Null(row.VectorModel);
    }

    [Fact]
    public async Task IndexNewPdfs_SecondRun_SkipsIndexed()
    {
        await CreatePdfAsync("Acme");
        var indexer = MakeIndexer();

        Assert.Equal(1, await indexer.IndexNewPdfsAsync());
        Assert.Equal(0, await indexer.IndexNewPdfsAsync());
        Assert.Single(_store.ListIndexedPdfs());
    }

    [Fact]
    public async Task IndexNewPdfs_MissingFolder_IsSkipped_WithWarn()
    {
        // осознанный пропуск с причиной — папки ещё нет (первый старт).
        var indexer = new PdfIndexerService(_store, _log,
            () => Path.Combine(_dir, "not-created"));

        Assert.Equal(0, await indexer.IndexNewPdfsAsync());
        Assert.Contains(_log.Logs, line => line.Contains("WARN") && line.Contains("ещё не создана"));
    }
}
