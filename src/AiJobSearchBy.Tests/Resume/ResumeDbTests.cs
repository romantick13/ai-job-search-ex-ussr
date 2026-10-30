using AiJobSearchBy.Services.Resume;
using Xunit;

namespace AiJobSearchBy.Tests.Resume;

// база профилей (SQLite + Dapper, канон architecture.md §4): CRUD, ровно один активный,
// перезапись ключевых, каскадное удаление.
public class ResumeDbTests : IDisposable
{
    private readonly string _dir;
    private readonly ResumeStore _store;

    public ResumeDbTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "resumedb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ResumeStore(Path.Combine(_dir, "resume.db"), new SilentLog());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void AddProfile_AndReadBack()
    {
        var added = _store.AddProfile("Сисадмин", "/tmp/resume.pdf", "hash1", "# Сисадмин\n## Навыки\nESXi", null);

        var profile = _store.GetProfile(added.Id);
        Assert.NotNull(profile);
        Assert.Equal("Сисадмин", profile!.Name);
        Assert.Contains("ESXi", profile.CanonMd);
        Assert.False(profile.IsActive);
    }

    [Fact]
    public void SetActive_ExactlyOneActive()
    {
        var a = _store.AddProfile("A", "", "h1", "AA", null);
        var b = _store.AddProfile("B", "", "h2", "BB", null);

        _store.SetActive(a.Id);
        _store.SetActive(b.Id);

        var profiles = _store.ListProfiles();
        Assert.Single(profiles, p => p.IsActive);
        Assert.Equal(b.Id, profiles.Single(p => p.IsActive).Id);
    }

    [Fact]
    public void ReplaceKeywords_FullRewrite()
    {
        var p = _store.AddProfile("QA", "", "h", "# QA", null);

        _store.ReplaceKeywords(p.Id, new[]
        {
            new ResumeKeywordRecord { ProfileId = p.Id, Kind = "my", Group = "Тестирование", Value = "Selenium", Norm = "selenium" },
            new ResumeKeywordRecord { ProfileId = p.Id, Kind = "extracted", Group = "Тестирование", Value = "Postman", Norm = "postman" },
        });

        Assert.Equal(2, _store.GetKeywords(p.Id).Count);

        // Снял галочку -> перезапись одним элементом.
        _store.ReplaceKeywords(p.Id, new[]
        {
            new ResumeKeywordRecord { ProfileId = p.Id, Kind = "my", Group = "Тестирование", Value = "Selenium", Norm = "selenium" },
        });
        var kw = _store.GetKeywords(p.Id);
        Assert.Single(kw);
        Assert.Equal("Selenium", kw[0].Value);
    }

    [Fact]
    public void AddKeyword_DuplicateByNorm_Ignored()
    {
        var p = _store.AddProfile("A", "", "h", "# A", null);
        _store.AddKeyword(p.Id, new ResumeKeywordRecord { ProfileId = p.Id, Value = "Docker", Norm = "docker" });
        _store.AddKeyword(p.Id, new ResumeKeywordRecord { ProfileId = p.Id, Value = "docker", Norm = "docker" });

        Assert.Single(_store.GetKeywords(p.Id));
    }

    [Fact]
    public void DeleteProfile_CascadeKeywordsAndFiles()
    {
        var p = _store.AddProfile("A", "", "h", "# A", null);
        _store.AddKeyword(p.Id, new ResumeKeywordRecord { ProfileId = p.Id, Value = "X", Norm = "x" });

        var filesDir = ResumeStore.FilesDir(_store.DbPath, p.Id);
        Directory.CreateDirectory(filesDir);
        File.WriteAllText(Path.Combine(filesDir, "source.pdf"), "x");

        _store.DeleteProfile(p.Id);

        Assert.Null(_store.GetProfile(p.Id));
        Assert.Empty(_store.GetKeywords(p.Id)); // каскад
        Assert.False(Directory.Exists(filesDir)); // файлы ушли
    }

    [Fact]
    public void UpdateCanon_AndDocxPath()
    {
        var p = _store.AddProfile("A", "/tmp/x.pdf", "h", "# A", null);
        var docx = Path.Combine(ResumeStore.FilesDir(_store.DbPath, p.Id), "resume.docx");

        _store.UpdateCanon(p.Id, "# A обновлён");
        _store.UpdateDocxPath(p.Id, docx);

        var fresh = _store.GetProfile(p.Id);
        Assert.Contains("обновлён", fresh!.CanonMd);
        Assert.Equal(docx, fresh.DocxPath);
    }
}
