using AiJobSearchBy.Models;
using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests.Logging;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: ApplicationStore (WP-U1) — схема §4 + EnsureColumn-расширения,
// INSERT OR IGNORE по DedupeHash, история откликов, PdfDocuments, SettingsKV.
// LIFECYCLE: каждый тест — своя temp-база (SQLite); состояния между тестами нет.
// THREAD: однопоточно.
public class ApplicationStoreTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();
    private readonly ApplicationStore _store;

    public ApplicationStoreTests()
    {
        _store = new ApplicationStore(
            Path.Combine(_dir, "test.db"), new LogService(_dir, echoToConsole: false));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private static JobPosting Posting(string portalId = "remoteok", string? externalId = "1",
        string url = "https://example.org/vacancy/1", string? company = "Acme") => new()
    {
        PortalId = portalId,
        ExternalId = externalId,
        CanonicalUrl = url,
        Title = "Senior Developer",
        Company = company,
        DedupeHash = DedupeHasher.ComputeComposite(portalId, externalId, url),
    };

    [Fact]
    public void Initialize_IsIdempotent()
    {
        // Двойной вызов миграций не падает и не дублирует (канон §4).
        _store.Initialize();
        _store.Initialize();
        Assert.True(File.Exists(_store.DbPath));
    }

    [Fact]
    public void UpsertVacancies_DedupesByDedupeHash()
    {
        var first = _store.UpsertVacancies([Posting(), Posting(url: "https://example.org/vacancy/2")]);

        Assert.Equal(2, first);

        // Повторный прогон по тем же вакансиям: INSERT OR IGNORE = 0 новеньких.
        var second = _store.UpsertVacancies([Posting()]);

        Assert.Equal(0, second);
    }

    [Fact]
    public void EnsureVacancy_ReturnsSameRowId_ForSameHash()
    {
        var posting = Posting();
        var id1 = _store.EnsureVacancy(posting);
        var id2 = _store.EnsureVacancy(posting with { Title = "обновлённое название" });

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void SaveApplication_RoundTrip_WithVector()
    {
        var vacancyId = _store.EnsureVacancy(Posting());
        var vector = new float[] { 0.5f, -1.25f, 3f };
        var record = new ApplicationRecord
        {
            Id = Guid.NewGuid(),
            VacancyId = vacancyId,
            Employer = "ООО Ромашка",
            VacancyTitle = "Senior Developer",
            VacancyUrl = "https://example.org/vacancy/1",
            Status = "sent",
            CvPath = @"C:\out\ООО Ромашка_20261022_141530.pdf",
            SentAt = new DateTimeOffset(2026, 10, 22, 14, 15, 30, TimeSpan.FromHours(3)),
            CreatedAt = new DateTimeOffset(2026, 10, 22, 14, 15, 30, TimeSpan.FromHours(3)),
            ResumeText = "адаптированное резюме",
            VectorJson = VectorJson.To(vector),
            VectorModel = "local-embedder-test",
        };

        _store.SaveApplication(record);
        var history = _store.RecentApplications(50);

        var row = Assert.Single(history);
        Assert.Equal("ООО Ромашка", row.Employer);
        Assert.Equal("sent", row.Status);
        Assert.Equal(vacancyId, row.VacancyId);
        Assert.Equal(record.SentAt, row.SentAt);
        Assert.Equal("адаптированное резюме", row.ResumeText);

        // Векторная ссылка: JSON туда-обратно без потерь.
        var restored = VectorJson.From(row.VectorJson);
        Assert.NotNull(restored);
        Assert.Equal(vector, restored);

        var byId = _store.GetApplication(record.Id);
        Assert.NotNull(byId);
        Assert.Equal(record.Employer, byId!.Employer);
    }

    [Fact]
    public void RecentApplications_OrdersBySentAtDesc()
    {
        var vacancyId = _store.EnsureVacancy(Posting());
        _store.SaveApplication(new ApplicationRecord
        {
            Id = Guid.NewGuid(), VacancyId = vacancyId, Employer = "старый",
            SentAt = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero),
            CreatedAt = DateTimeOffset.Now,
        });
        _store.SaveApplication(new ApplicationRecord
        {
            Id = Guid.NewGuid(), VacancyId = vacancyId, Employer = "свежий",
            SentAt = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero),
            CreatedAt = DateTimeOffset.Now,
        });

        var history = _store.RecentApplications(50);

        Assert.Equal("свежий", history[0].Employer);
        Assert.Equal("старый", history[1].Employer);
    }

    [Fact]
    public void PdfIndex_UpsertsByFilePath()
    {
        var path = Path.Combine(_dir, "ООО Ромашка_20261022_141530.pdf");
        _store.SavePdfIndex(new PdfDocumentRecord
        {
            Id = Guid.NewGuid(),
            FilePath = path,
            FileHash = "AAA",
            ExtractedText = "текст резюме",
            VectorJson = null,
            VectorModel = null,
            IndexedAt = DateTimeOffset.Now,
        });
        // Повторная индексация того же файла (пересохранён) — апдейт, не дубль.
        _store.SavePdfIndex(new PdfDocumentRecord
        {
            Id = Guid.NewGuid(),
            FilePath = path,
            FileHash = "BBB",
            ExtractedText = "новый текст",
            VectorJson = "[1,2]",
            VectorModel = "local-embedder-test",
            IndexedAt = DateTimeOffset.Now,
        });

        var all = _store.ListIndexedPdfs();

        var row = Assert.Single(all);
        Assert.Equal("BBB", row.FileHash);
        Assert.Equal("[1,2]", row.VectorJson);
    }

    [Fact]
    public void SettingsKV_RoundTrip()
    {
        Assert.Null(_store.GetSetting("resume.text"));

        _store.SetSetting("resume.text", "моё резюме");
        _store.SetSetting("resume.text", "моё резюме v2");

        Assert.Equal("моё резюме v2", _store.GetSetting("resume.text"));
    }
}
