using AiJobSearchExUssr.Models;
using AiJobSearchExUssr.Services;
using AiJobSearchExUssr.Tests.Logging;
using Xunit;

namespace AiJobSearchExUssr.Tests;

// FLOW: UpdateApplicationResume — точечное обновление
// отправленного отклика: правленный текст резюме + пересобранный PDF.
// LIFECYCLE: каждый тест — своя temp-база (SQLite); состояния между тестами нет.
// THREAD: однопоточно.
public class ApplicationStoreResumeEditTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();
    private readonly ApplicationStore _store;

    public ApplicationStoreResumeEditTests()
    {
        _store = new ApplicationStore(
            Path.Combine(_dir, "test.db"), new LogService(_dir, echoToConsole: false));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    private ApplicationRecord SentApplication()
    {
        var url = "https://example.org/vacancy/1";
        var vacancyId = _store.EnsureVacancy(new JobPosting
        {
            PortalId = "remoteok",
            ExternalId = "1",
            CanonicalUrl = url,
            Title = "Senior Developer",
            Company = "ООО Ромашка",
            DedupeHash = DedupeHasher.ComputeComposite("remoteok", "1", url),
        });
        var record = new ApplicationRecord
        {
            Id = Guid.NewGuid(),
            VacancyId = vacancyId,
            Employer = "ООО Ромашка",
            VacancyTitle = "Senior Developer",
            VacancyUrl = url,
            Status = "sent",
            CvPath = @"C:\out\ООО Ромашка_20261022_141530.pdf",
            SentAt = new DateTimeOffset(2026, 10, 22, 14, 15, 30, TimeSpan.FromHours(3)),
            CreatedAt = new DateTimeOffset(2026, 10, 22, 14, 15, 30, TimeSpan.FromHours(3)),
            ResumeText = "первый вариант резюме",
        };
        _store.SaveApplication(record);
        return record;
    }

    [Fact]
    public void UpdateApplicationResume_UpdatesTextAndCvPath()
    {
        var record = SentApplication();

        var saved = _store.UpdateApplicationResume(
            record.Id, "правленный текст резюме", @"C:\out\ООО Ромашка_20261023_090000.pdf");

        Assert.True(saved);
        var row = _store.GetApplication(record.Id);
        Assert.NotNull(row);
        Assert.Equal("правленный текст резюме", row!.ResumeText);
        Assert.Equal(@"C:\out\ООО Ромашка_20261023_090000.pdf", row.CvPath);
        // Точечность: статус, работодатель и метаданные отправки не трогаются.
        Assert.Equal("sent", row.Status);
        Assert.Equal("ООО Ромашка", row.Employer);
        Assert.Equal(record.SentAt, row.SentAt);
    }

    [Fact]
    public void UpdateApplicationResume_ReturnsFalse_ForUnknownId()
    {
        // Контракт «никаких тихих игноров»: строка не найдена — вызывающий код
        // получает false и обязан отреагировать (окно кричит в протокол).
        var saved = _store.UpdateApplicationResume(Guid.NewGuid(), "текст", null);

        Assert.False(saved);
    }

    [Fact]
    public void UpdateApplicationResume_CanClearCvPath()
    {
        var record = SentApplication();

        var saved = _store.UpdateApplicationResume(record.Id, "второй вариант", null);

        Assert.True(saved);
        var row = _store.GetApplication(record.Id);
        Assert.NotNull(row);
        Assert.Equal("второй вариант", row!.ResumeText);
        Assert.Null(row.CvPath);
    }
}
