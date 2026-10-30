using AiJobSearchBy.Services;
using AiJobSearchBy.Services.Ai;
using AiJobSearchBy.Tests.Logging;
using AiJobSearchBy.ViewModels;
using Xunit;

namespace AiJobSearchBy.Tests;

// FLOW: MainViewModel -> тесты (смоук MVVM-генераторов + гейты)
// LIFECYCLE: каждый тест строит свою модель и свой tmp-каталог протокола;
// состояния между тестами нет
// STATE: смоук MVVM-генераторов CommunityToolkit —
// [ObservableProperty] обязан поднимать PropertyChanged. LogFeed —
// история из LogService; Providers — флаги цепи из конкретных типов.
// THREAD: однопоточно — UI-поток не нужен; живую ленту (диспетчер) тесты
// не трогают — только историю, скопированную в конструкторе.
public class MainViewModelTests : IDisposable
{
    private readonly string _dir = LogServiceTests.TempLogDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка тестов */ }
    }

    [Fact]
    public void Greeting_IsNotEmpty()
    {
        var viewModel = new MainViewModel();

        Assert.False(string.IsNullOrWhiteSpace(viewModel.Greeting));
    }

    [Fact]
    public void Greeting_Set_RaisesPropertyChangedOnce()
    {
        var viewModel = new MainViewModel();
        var events = new List<string>();
        viewModel.PropertyChanged += (_, e) => events.Add(e.PropertyName ?? "");

        viewModel.Greeting = "новый текст";

        var greetingEvents = events.Where(n => n == nameof(MainViewModel.Greeting));
        Assert.Single(greetingEvents);
    }

    [Fact]
    public void LogFeed_History_Copied_From_LogService()
    {
        // Дашборд стартует с историей протокола — пользователь видит не
        // пустой лист, а всё, что уже произошло (старт, [config], провайдеры)
        var log = new LogService(_dir, echoToConsole: false);
        log.Log("строка А");
        log.Log("строка Б");

        var vm = new MainViewModel(log, providers: null);

        Assert.Equal(2, vm.LogFeed.Count);
        Assert.Contains(vm.LogFeed, x => x.Contains("строка А"));
        Assert.Contains(vm.LogFeed, x => x.Contains("строка Б"));
    }

    [Fact]
    public void Providers_ReflectChainState_FromConcreteTypes()
    {
        var log = new LogService(_dir, echoToConsole: false);

        // Урок: Llm7Provider(apiKey: null) читает AIJOB_LLM7_API_KEY —
        // чужое окно ConfigServiceTests в параллельном классе (и реальный ключ
        // машины) делало llm7 «неанонимным», карточка врала «ключ: токен».
        // Гвард гасит переменную на время конструкторов и сериализует окно.
        IAiChatProvider cloudNoKey, localNoKey, llm7Anonymous;
        using (TestEnvGuard.Scope("AIJOB_LLM7_API_KEY"))
        {
            cloudNoKey = new OpenAiCompatibleProvider(
                log, "test-cloud", "https://example.org/v1/", requireApiKey: true);
            localNoKey = new OpenAiCompatibleProvider(
                log, "test-local", "http://localhost:9/v1/", requireApiKey: false);
            llm7Anonymous = new Llm7Provider(log, apiKey: null, budgetFilePath: null);
        }

        var vm = new MainViewModel(null, new IAiChatProvider[]
        {
            cloudNoKey, localNoKey, llm7Anonymous,
        });

        Assert.Equal(3, vm.Providers.Count);

        // встроенные звенья включены по умолчанию (чекбокс карточки);
        // облачный без ключа — честная карточка «ключ НЕТ», проверка покажет
        Assert.Equal("test-cloud", vm.Providers[0].Name);
        Assert.True(vm.Providers[0].InChain);
        Assert.False(vm.Providers[0].KeyPresent);
        Assert.Contains("ключ НЕТ", vm.Providers[0].KeyNote);

        // локальный: без ключа, но в цепи
        Assert.True(vm.Providers[1].InChain);
        Assert.Contains("локальный", vm.Providers[1].KeyNote);

        // llm7 анонимный: ключа нет, но из цепи не выпадает
        Assert.Equal("llm7", vm.Providers[2].Name);
        Assert.False(vm.Providers[2].KeyPresent);
        Assert.True(vm.Providers[2].InChain);
    }

    [Fact]
    public void Parameterless_Ctor_Empty_Collections_ForDesigner()
    {
        // Дизайнер XAML/фикстуры: модель без зависимостей не падает и не врёт
        var vm = new MainViewModel();

        Assert.Empty(vm.Providers);
        Assert.Empty(vm.LogFeed);
    }
}
