using AiJobSearchBy.Models;
using AiJobSearchBy.Portals;
using AiJobSearchBy.Services;
using AiJobSearchBy.ViewModels;
using Xunit;

namespace AiJobSearchBy.Tests;

// ============================================================================
// Гейты темпа обхода выдачи группы hh:
// 1) каждая следующая страница — НОВАЯ ВКЛАДКА (Referer = первая страница,
//    цепочки «стр -> стр» нет) с рваной паузой 5-25 с; все страницы — в кэш;
//    итог фазы открытия — итоговая строка протокола;
// 2) одна плохая страница кэша НЕ убивает портал — пропуск с warn,
//    остальные парсятся (мгновенная скачка по страницам приводит к щиту
//    сайта: вместо списка приходит заглушка без карточек);
// 3) «Отмена» на долгой фазе открытия — кэш добирается и парсится;
// 4) счётчик дедупа прогона (LastRunDuplicatesExcluded) + счётчик окна
//    «найдено X вакансий, дублей исключено Y» (крестик уменьшает).
// Сети нет — FakeHttpHandler/SilentLog; паузы в тестах = Pacing.Silent.
// ============================================================================
public class SearchTabPacingTests
{
    private static string Card(string id, string title, string company)
        => "<div id=\"" + id + "\" class=\"vacancy-card--xXyY\"><div class=\"vacancy-info--x\">"
           + "<a data-qa=\"serp-item__title\" href=\"https://rabota.by/vacancy/" + id
           + "\" target=\"blank\"><span data-qa=\"serp-item__title-text\">" + title + "</span></a>"
           + "<a data-qa=\"vacancy-serp__vacancy-employer\" href=\"/employer/" + id
           + "\"><span data-qa=\"vacancy-serp__vacancy-employer-text\">" + company + "</span></a>"
           + "</div></div>";

    // Первая страница: счётчик «Найдено N вакансий» + карточки + нумерованный
    // пагинатор (0-базный) на все страницы плана.
    private static string SerpFirst(int found, params string[] cards)
    {
        var perPage = Math.Max(cards.Length, 1);
        var pages = Math.Max(1, (int)Math.Ceiling(found / (double)perPage));
        var pager = string.Join(string.Empty, Enumerable.Range(0, pages)
            .Select(p => "<li><a data-qa=\"pager-page\" href=\"/search/vacancy?text=a&amp;area=16&amp;page="
                         + p + "\">" + (p + 1) + "</a></li>"));
        return "<html><body><h1 data-qa=\"title\"><span>Найдено " + found
               + " вакансий</span></h1>" + string.Concat(cards)
               + "<nav data-qa=\"pager-block\"><ul>" + pager + "</ul></nav></body></html>";
    }

    private static (HhGroupPortal Portal, SilentLog Log, FakeHttpHandler Handler) Create(
        FakeHttpHandler handler)
    {
        var log = new SilentLog();
        var portal = new HhGroupPortal(BuiltInPortals.HhBy, new HttpClient(handler), log,
            BrowserEmulationPortalBase.Pacing.Silent);
        return (portal, log, handler);
    }

    // page=2 проверяем РАНЬШЕ page=: подстрока "page=" есть в обеих.
    private static FakeHttpHandler Handler3(string first, string second, string third)
        => new(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("page=2"))
            {
                return FakeHttpHandler.Of(third, "text/html");
            }

            return url.Contains("page=")
                ? FakeHttpHandler.Of(second, "text/html")
                : FakeHttpHandler.Of(first, "text/html");
        });

    [Fact]
    public async Task Search_EveryNextPageOpenedInNewTab_RefererIsFirstPage()
    {
        // 5 вакансий по 2 карточки = 3 страницы: страницы 2 и 3 открываются
        // НОВЫМИ ВКЛАДКАМИ ИЗ первой страницы (Referer первой, не цепочкой).
        var handler = Handler3(
            SerpFirst(5, Card("100", "Администратор", "ООО Ромашка"),
                Card("101", "Сисадмин", "ООО Тюльпан")),
            SerpFirst(0, Card("102", "DevOps инженер", "ООО Ландыш")),
            SerpFirst(0, Card("103", "SRE", "ООО Клён")));
        var (portal, log, _) = Create(handler);

        var postings = await portal.SearchAsync("администратор");

        Assert.Equal(4, postings.Count);
        Assert.Equal(3, handler.RequestCount);
        // Заголовок добавлен TryAddWithoutValidation — читаем строкой; HttpClient
        // нормализует Referer как Uri (раскодирует text=), поэтому сверяем
        // семантику: обе вкладки открыты ИЗ первой страницы (без page=).
        static string Referer(HttpRequestMessage req)
            => req.Headers.TryGetValues("Referer", out var v) ? v.First() : "";
        var refererTab2 = Referer(handler.Requests[1]);
        Assert.StartsWith("https://rabota.by/search/vacancy", refererTab2);
        Assert.DoesNotContain("page=", refererTab2);
        Assert.Equal(refererTab2, Referer(handler.Requests[2]));
        // Каждое открытие вкладки — строкой в протоколе (пауза Pacing.Silent = 0 с).
        Assert.Contains(log.Logs, l => l.Contains("вкладка 2/3"));
        Assert.Contains(log.Logs, l => l.Contains("вкладка 3/3"));
        // ИТОГОВАЯ строка фазы открытия — все страницы плана в кэше.
        Assert.Contains(log.Logs,
            l => l.Contains("все страницы выдачи открыты вкладками и в кэше — 3"));
    }

    [Fact]
    public async Task Search_BadCachedPage_SkippedWithWarn_OthersParsed()
    {
        // Страница 2 в кэше — щит/мусор без карточек: ParseFailed глотается,
        // страницы 1 и 3 парсятся, портал НЕ падает (урок).
        var handler = Handler3(
            SerpFirst(5, Card("100", "Администратор", "ООО Ромашка"),
                Card("101", "Сисадмин", "ООО Тюльпан")),
            "<html><body><div class='captcha'>подтвердите, что вы человек</div></body></html>",
            SerpFirst(0, Card("103", "SRE", "ООО Клён")));
        var (portal, log, _) = Create(handler);

        var postings = await portal.SearchAsync("администратор");

        Assert.Equal(3, postings.Count); // 2 с первой + 1 с третьей
        Assert.Contains(log.Logs, l => l.Contains("страница 2 из кэша не распарсилась"));
        Assert.Contains(log.Logs, l => l.Contains("парсю остальные страницы кэша"));
        Assert.Contains(log.Logs, l => l.Contains("обход выдачи завершён — вакансий: 3"));
    }

    [Fact]
    public async Task Search_CancelledDuringOpening_ParsesCachedPages_ReturnsCollected()
    {
        // «Отмена» на фазе открытия (теперь она долгая — до пары минут):
        // скачанные, но ещё не распарсенные страницы кэша ДОБИРАЮТСЯ и
        // парсятся — наработанное не пропадает. Обрываем на вкладке 3/3:
        // страницы 1-2 уже в кэше, парсинг ещё не начинался.
        using var cts = new CancellationTokenSource();
        var first = SerpFirst(5, Card("100", "Администратор", "ООО Ромашка"),
            Card("101", "Сисадмин", "ООО Тюльпан"));
        var second = SerpFirst(0, Card("102", "DevOps инженер", "ООО Ландыш"));
        var handler = new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("page=2"))
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            return url.Contains("page=")
                ? FakeHttpHandler.Of(second, "text/html")
                : FakeHttpHandler.Of(first, "text/html");
        });
        var (portal, log, _) = Create(handler);

        var postings = await portal.SearchAsync("администратор", cts.Token);

        Assert.Equal(3, postings.Count); // страница 1 (2) + страница 2 (1) из кэша
        Assert.Contains(log.Logs, l => l.Contains("добираю страницы 1-2 из кэша"));
        Assert.Contains(log.Logs, l => l.Contains("отдаю собранное: 3 вакансий"));
    }

    [Fact]
    public void Pacing_Human_TabOpenPause_Is5To25()
    {
        // Директива «таймаутами от 5 до 25 секунд, через рваные
        // промежутки» ; Silent (тесты) — нули.
        Assert.Equal(5.0, BrowserEmulationPortalBase.Pacing.Human.TabOpenMinSeconds);
        Assert.Equal(25.0, BrowserEmulationPortalBase.Pacing.Human.TabOpenMaxSeconds);
        Assert.Equal(0, BrowserEmulationPortalBase.Pacing.Silent.TabOpenMinSeconds);
        Assert.Equal(0, BrowserEmulationPortalBase.Pacing.Silent.TabOpenMaxSeconds);
    }

    [Fact]
    public async Task Search_TitleKeyDedup_CountsDuplicatesForUiCounter()
    {
        // Перепост отсекается в обходе и попадает в LastRunDuplicatesExcluded —
        // счётчик у окна «Вакансии» («дублей исключено»).
        var duplicate = SerpFirst(2,
            Card("100", "Администратор", "ООО Ромашка"),
            Card("999", "Администратор", "ООО Ромашка"));
        var (portal, log, _) = Create(Handler3(duplicate, duplicate, duplicate));

        var postings = await portal.SearchAsync("администратор");

        var posting = Assert.Single(postings);
        Assert.Equal("100", posting.ExternalId);
        Assert.Equal(1, portal.LastRunDuplicatesExcluded);
        Assert.Contains(log.Logs, l => l.Contains("дубль названия и работодателя"));
    }
}

// ============================================================================
// счётчик у окна «Вакансии» — «найдено XXX вакансий, XX дубля
// исключено (оранжевым)»; крестик уменьшает «найдено», дубли копятся.
// ============================================================================
public class VacancyCounterTests
{
    private static JobPosting Posting(string id, string title, string company)
        => new()
        {
            PortalId = "hh-by",
            ExternalId = id,
            CanonicalUrl = $"https://rabota.by/vacancy/{id}",
            Title = title,
            Company = company,
            DedupeHash = $"h-{id}",
        };

    [Fact]
    public void Counter_FoundShrinksOnDelete_DupsCounted()
    {
        var vm = new MainViewModel(new SilentLog(), providers: null);

        var applied = vm.ApplyVacancies(
        [
            Posting("1", "Администратор", "ООО Ромашка"),
            Posting("2", "Сисадмин", "ООО Тюльпан"),
            Posting("3", "Администратор", "ООО Ромашка"), // перепост: то же название+работодатель
        ], null);

        Assert.Equal(2, applied.Added);
        Assert.Equal(1, vm.DuplicatesExcluded);
        Assert.True(vm.HasDuplicatesExcluded);
        Assert.Equal("дублей исключено: 1", vm.VacancyDupText);
        Assert.Equal("найдено 2 вакансии", vm.VacancyFoundText);
        Assert.True(vm.HasVacancies);

        // Крестик: «найдено» убывает, «дублей исключено» не трогается.
        var row = vm.Vacancies.First(r => r.Posting.ExternalId == "2");
        vm.DeleteVacancyCommand.Execute(row);
        Assert.Equal("найдено 1 вакансия", vm.VacancyFoundText);
        Assert.Equal(1, vm.DuplicatesExcluded);
        Assert.True(vm.HasVacancies);
    }

    [Fact]
    public void Counter_EmptyWindow_HidesCounter()
    {
        var vm = new MainViewModel(new SilentLog(), providers: null);

        Assert.Equal(string.Empty, vm.VacancyFoundText);
        Assert.Equal(string.Empty, vm.VacancyDupText);
        Assert.False(vm.HasDuplicatesExcluded);
        Assert.False(vm.HasVacancies);
    }

    [Fact]
    public void VacanciesPlural_RussianGrammar()
    {
        Assert.Equal("1 вакансия", MainViewModel.VacanciesPlural(1));
        Assert.Equal("2 вакансии", MainViewModel.VacanciesPlural(2));
        Assert.Equal("5 вакансий", MainViewModel.VacanciesPlural(5));
        Assert.Equal("11 вакансий", MainViewModel.VacanciesPlural(11));
        Assert.Equal("12 вакансий", MainViewModel.VacanciesPlural(12));
        Assert.Equal("14 вакансий", MainViewModel.VacanciesPlural(14));
        Assert.Equal("21 вакансия", MainViewModel.VacanciesPlural(21));
        Assert.Equal("22 вакансии", MainViewModel.VacanciesPlural(22));
        Assert.Equal("25 вакансий", MainViewModel.VacanciesPlural(25));
        Assert.Equal("101 вакансия", MainViewModel.VacanciesPlural(101));
        Assert.Equal("111 вакансий", MainViewModel.VacanciesPlural(111));
        Assert.Equal("159 вакансий", MainViewModel.VacanciesPlural(159));
    }
}
