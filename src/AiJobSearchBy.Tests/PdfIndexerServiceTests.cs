using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: PdfIndexerService (WP-U1) — «первый запуск векторизует все PDF, которые
// мы создаём» (требование владельца). Новые файлы -> текст (PdfPig) -> вектор
// (локальный эмбеддер, WP-U2) -> PdfDocuments; повторный прогон не дублирует;
// смена модели эмбеддера — пере-векторизация чужих векторов из базы.
// THREAD: FakeEmbeddings вместо сети.
public class PdfIndexerServiceTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();
    private readonly string _outbox;
    private readonly ApplicationStore _store;
    private readonly FakeEmbeddings _embeddings = new();
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
        => new(_store, _embeddings, _log, () => _outbox);

    private async Task<string> CreatePdfAsync(string employer)
    {
        var exporter = new PdfExportService(new LogService(_dir, echoToConsole: false));
        return await exporter.SaveResumePdfAsync(employer, "Senior Developer",
            "Текст резюме для индексации", DateTimeOffset.Now, _outbox);
    }

    [Fact]
    public async Task IndexNewPdfs_ExtractsText_AndWritesVectorLink()
    {
        await CreatePdfAsync("ООО Ромашка");

        var done = await MakeIndexer().IndexNewPdfsAsync();

        Assert.Equal(1, done);
        var row = Assert.Single(_store.ListIndexedPdfs());
        Assert.Equal("ООО Ромашка", Path.GetFileNameWithoutExtension(row.FilePath).Split('_')[0]);
        Assert.False(string.IsNullOrWhiteSpace(row.ExtractedText));
        Assert.Contains("Текст резюме", row.ExtractedText);
        // Векторная ссылка в базе (фейковый эмбеддер, конвейер тот же).
        Assert.Equal("[0.1,0.2]", row.VectorJson);
        Assert.Equal("fake-embeddings", row.VectorModel);
    }

    [Fact]
    public async Task IndexNewPdfs_ReembedsStaleVectors_UnderCurrentModel()
    {
        // WP-U2: векторы сравнимы только внутри одного пространства — смена
        // VectorModel делает старые несравнимыми. Индексатор обязан перевести
        // их на текущую модель ИЗ БАЗЫ (PDF не перечитывается).
        await CreatePdfAsync("Acme");
        await new PdfIndexerService(_store, new FakeEmbeddings("jina-embeddings-v3"),
            _log, () => _outbox).IndexNewPdfsAsync();
        var stale = Assert.Single(_store.ListIndexedPdfs());
        Assert.Equal("jina-embeddings-v3", stale.VectorModel);

        // Эмбеддер сменился: новых файлов нет, но чужие векторы переезжают.
        var done = await MakeIndexer().IndexNewPdfsAsync();

        Assert.Equal(0, done);
        var fresh = Assert.Single(_store.ListIndexedPdfs());
        Assert.Equal("fake-embeddings", fresh.VectorModel);
        Assert.Equal("[0.1,0.2]", fresh.VectorJson);
        Assert.Contains(_log.Logs, line => line.Contains("векторы переведены на fake-embeddings"));
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
        // НИ-2: осознанный пропуск с причиной — папки ещё нет (первый старт).
        var indexer = new PdfIndexerService(_store, _embeddings, _log,
            () => Path.Combine(_dir, "not-created"));

        Assert.Equal(0, await indexer.IndexNewPdfsAsync());
        Assert.Contains(_log.Logs, line => line.Contains("WARN") && line.Contains("ещё не создана"));
    }

    // Фейк вместо локального эмбеддера: тот же контракт, детерминированный вектор.
    private sealed class FakeEmbeddings : IEmbeddingService
    {
        public FakeEmbeddings(string model = "fake-embeddings") => Model = model;

        public bool Enabled => true;
        public string Model { get; }

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([0.1f, 0.2f]);

        public Task<IReadOnlyList<float[]>?> EmbedBatchAsync(IReadOnlyList<string> texts,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<float[]>?>(texts.Select(_ => new float[] { 0.1f, 0.2f }).ToList());
    }
}
