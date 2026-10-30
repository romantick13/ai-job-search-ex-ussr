using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiJobSearchBy.Services.Resume;

// ============================================================================
// словарь ключевых слов — ФАЙЛ в
// конфиг-каталоге, рядом с local.json и state\:
// C:\Users\<юзер>\ai-job-search-by\keywords.json
// Формула «пусть этот список сохраняется в конфиг рядом с другими,
// чтобы чел сам взял, один раз его под себя отредактировал и забыл».

// Правила файла:
// - нет файла -> создаём из встроенного дефолта (124+ позиции:
// значимые языки, рантаймы, стеки, скрипты, админ,
// облака, базы + УЧЁТ И ОФИС (1С для бухгалтеров),
// роли) — из коробки работает и для бухгалтерских запросов;
// - файл есть -> читаем, НЕ перетираем никогда (обновления
// приложения дефолтом файл не трогают);
// - файл битый -> LogError + работаем на дефолте; юзерский файл
// остаётся на диске (чинит руками);
// - «＋ Добавить» -> атомарно дописываем в файл (tmp -> replace).

// FLOW: ResumeProfilesViewModel (Каталог/Выбранные/Добавить) + KeywordExtractor.
// STATE: Categories — снимок последней загрузки; мутации через AddItemAsync.
// THREAD: операции файла — под _gate (одна запись SQLite-файла в момент).
// ============================================================================

// Категория словаря: имя + элементы (порядок сохраняем как в файле).
public sealed class KeywordCategory
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();
}

public sealed class KeywordCatalogFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("categories")]
    public List<KeywordCategory> Categories { get; set; } = new();
}

public interface IKeywordDictionary
{
    string FilePath { get; }
    IReadOnlyList<KeywordCategory> Categories { get; }
    int TotalItems { get; }

    // Читает файл (или создаёт из дефолта). Возвращает true, если файл создан.
    bool Load();

    // Все элементы одной плоской пачкой (для экстрактора и поиска).
    IReadOnlyList<(string Value, string Category)> Flat();

    // Добавить элемент в категорию (создать категорию при необходимости)
    // и атомарно записать файл. false — элемент уже был.
    bool AddItem(string category, string value);
}

public sealed class KeywordDictionary : IKeywordDictionary
{
    private readonly object _gate = new();
    private readonly ILogService _log;
    private List<KeywordCategory> _categories = new();

    public string FilePath { get; }

    public IReadOnlyList<KeywordCategory> Categories => _categories;

    public int TotalItems
    {
        get
        {
            lock (_gate)
            {
                return _categories.Sum(c => c.Items.Count);
            }
        }
    }

    public KeywordDictionary(string filePath, ILogService log)
    {
        FilePath = filePath;
        _log = log;
    }

    public bool Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var parsed = JsonSerializer.Deserialize<KeywordCatalogFile>(json);
                    if (parsed?.Categories is { Count: > 0 })
                    {
                        _categories = parsed.Categories.ToList();
                        _log.Log($"[словарь] keywords.json прочитан: {_categories.Count} категорий, " +
                                 $"{TotalItems} позиций ({FilePath})");
                        return false;
                    }

                    // Файл есть, но пустой/не той формы — кричим, работаем на дефолте,
                    // юзерский файл НЕ трогаем (он починит руками).
                    _log.LogError($"[словарь] keywords.json не содержит категорий — " +
                                  $"работаю на встроенном дефолте; файл не тронут: {FilePath}; " +
                                  "решение: восстановить JSON или удалить файл — создастся заново");
                    _categories = DefaultCatalog().ToList();
                    return false;
                }

                // Первого старта нет файла: создаём из дефолта — юзер сразу
                // видит путь в протоколе и правит под себя.
                _categories = DefaultCatalog().ToList();
                WriteFileLocked();
                _log.LogSuccess($"[словарь] создан дефолтный словарь: {TotalItems} позиций -> {FilePath} " +
                                "(правь под себя — приложение его не перетирает)");
                return true;
            }
            catch (Exception ex)
            {
                // сбой чтения не роняет приложение; дефолт — в памяти.
                _log.LogError($"[словарь] keywords.json не прочитан: {ex.Message} — " +
                              "работаю на встроенном дефолте; файл не тронут; " +
                              "решение: проверить JSON в файле или удалить его для пересоздания");
                _categories = DefaultCatalog().ToList();
                return false;
            }
        }
    }

    public IReadOnlyList<(string Value, string Category)> Flat()
    {
        lock (_gate)
        {
            return _categories
                .SelectMany(c => c.Items.Select(i => (Value: i, Category: c.Name)))
                .ToList();
        }
    }

    public bool AddItem(string category, string value)
    {
        var valueTrim = value.Trim();
        if (valueTrim.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var norm = GeoMatcher.Normalize(valueTrim);
            var already = _categories.Any(c => c.Items.Any(i => GeoMatcher.Normalize(i) == norm));
            if (already)
            {
                return false;
            }

            var cat = _categories.FirstOrDefault(c => string.Equals(
                GeoMatcher.Normalize(c.Name), GeoMatcher.Normalize(category), StringComparison.Ordinal));
            if (cat is null)
            {
                cat = new KeywordCategory { Name = category.Trim() };
                _categories.Add(cat);
            }

            cat.Items.Add(valueTrim);
            WriteFileLocked();
            return true;
        }
    }

    // Атомарная запись: tmp -> File.Replace/Move (падение на полпути не рвёт файл).
    private void WriteFileLocked()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new KeywordCatalogFile { Version = 1, Categories = _categories.ToList() };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, options));
        if (File.Exists(FilePath))
        {
            File.Replace(tmp, FilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, FilePath);
        }
    }


    // Встроенный дефолт (создаётся ОДИН раз в файл; дальше живёт юзерский).
    // Состав: значимые языки/рантаймы/стеки/скрипты/админ/облака/базы +
    // учёт и офис (1С — ответ на «бухгалтер из коробки
    // сможет юзать») + роли. Пополняется только руками юзера.

    public static List<KeywordCategory> DefaultCatalog() => new List<KeywordCategory>
    {
        new()
        {
            Name = "Языки программирования",
            Items = new List<string>
            {
                "C#", "Java", "Python", "JavaScript", "TypeScript", "PHP",
                "Go", "Rust", "Kotlin", "Swift", "Ruby", "C++", "Delphi",
            },
        },
        new()
        {
            Name = "Рантаймы и платформы",
            Items = new List<string>
            {
                ".NET", ".NET Framework", ".NET Core", "Node.js", "JVM",
                "Android", "iOS", "Flutter", "Xamarin", "Unity",
            },
        },
        new()
        {
            Name = "Стеки и фреймворки",
            Items = new List<string>
            {
                "ASP.NET Core", "Blazor", "WPF", "Spring", "React", "Vue",
                "Angular", "Laravel", "Django", "FastAPI", "1С-разработка",
            },
        },
        new()
        {
            Name = "Скрипты и оболочки",
            Items = new List<string>
            {
                "PowerShell", "Bash", "Python-скрипты", "VBScript", "Zsh",
            },
        },
        new()
        {
            Name = "Администрирование",
            Items = new List<string>
            {
                "Windows Server", "Active Directory", "GPO", "DNS", "DHCP",
                "IIS", "Exchange", "Zabbix", "Grafana", "Veeam (бэкапы)",
                "Acronis", "Linux", "Debian", "Ubuntu", "CentOS", "Astra Linux",
                "Nginx", "Apache", "GitLab CI", "GitHub Actions", "Jenkins",
                "Ansible", "Terraform",
            },
        },
        new()
        {
            Name = "Облака и виртуализация",
            Items = new List<string>
            {
                "Docker", "Kubernetes", "VMware ESXi", "vSphere", "Hyper-V",
                "Proxmox", "Azure", "AWS", "Yandex Cloud",
            },
        },
        new()
        {
            Name = "Базы данных",
            Items = new List<string>
            {
                "PostgreSQL", "MS SQL", "MySQL", "SQLite", "Oracle",
                "Redis", "MongoDB", "ClickHouse",
            },
        },
        new()
        {
            Name = "Тестирование",
            Items = new List<string>
            {
                "Postman", "Selenium", "Playwright", "TestRail", "Jira",
                "Нагрузочное тестирование",
            },
        },
        new()
        {
            // Решение: «бухгалтер 1С из коробки» —
            // учёт и офисные специальности в дефолте, а не только ИТ.
            Name = "Учёт и офис",
            Items = new List<string>
            {
                "1С:Предприятие 8", "1С:Бухгалтерия", "1С:ЗУП", "1С:УТ",
                "СКД", "ЭДО", "Банк-клиент", "Налоговая отчётность",
                "Мини-АСК", "Excel", "Word", "КонсультантПлюс", "СБИС",
                "МойСклад", "CRM", "Битрикс24",
            },
        },
        new()
        {
            Name = "Роли и специальности",
            Items = new List<string>
            {
                "Системный администратор", "Тестировщик QA", "Разработчик",
                "DevOps", "Сисоп", "Техподдержка", "Helpdesk",
                "Бухгалтер", "Бухгалтер 1С", "Экономист", "Кадровик",
                "Менеджер продаж", "Промышленный альпинизм",
            },
        },
    };
}
