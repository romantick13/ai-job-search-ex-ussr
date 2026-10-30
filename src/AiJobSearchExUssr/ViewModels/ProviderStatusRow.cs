using CommunityToolkit.Mvvm.ComponentModel;

namespace AiJobSearchExUssr.ViewModels;

// Состояние лампы карточки звена:
// NotChecked — серая (ещё не проверяли), Ok — зелёная, Failed — красная.
public enum ChainCheckState
{
    NotChecked,
    Ok,
    Failed,
}

// Строка карточки звена ИИ-цепи (шапка списка провайдеров).
// Снимок конкретных типов — при старте VM (Name/Model/KeyPresent —
// immutable, сурфейс только у конкретных типов провайдеров); состояние
// проверки и чекбокс — мутабельны. Лампа слева = результат «Проверить
// всех» (серая/зелёная/красная), чекбокс справа = звено включено в цепь
// (включённые звенья персистятся, встроенные включены по умолчанию).
public sealed class ProviderStatusRow : ObservableObject
{
    // Идентичность звена: имя провайдера (встроенные) / id кастомного звена.
    public string LinkId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public bool KeyPresent { get; init; }
    public bool IsCustom { get; init; }

    // адрес и требование ключа — для живой пробы (ChainProbe).
    public string BaseUrl { get; init; } = "";
    public bool RequiresKey { get; init; }

    // Кастомные звенья держат ключ прямо в карточке (из Chain.Custom);
    // встроенные берут СВЕЖИЙ ключ лестницей env -> local.json на проверке —
    // «подкрутил настройки — проверка ок/неудача» работает без перезапуска.
    public string ApiKey { get; init; } = "";

    private bool _inChain;
    public bool InChain
    {
        get => _inChain;
        set
        {
            if (SetProperty(ref _inChain, value))
            {
                OnPropertyChanged(nameof(IsEnabled));
                Toggled?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // Чекбокс карточки: звено включено в цепь (подписка VM — персистенция).
    public bool IsEnabled { get => InChain; set => InChain = value; }

    // Событие переключения чекбокса (MainViewModel пишет chain.disabled в базу).
    public event EventHandler? Toggled;

    private string _keyNote = "";
    public string KeyNote { get => _keyNote; set => SetProperty(ref _keyNote, value); }

    private ChainCheckState _state = ChainCheckState.NotChecked;
    public ChainCheckState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsOk));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    // Лампа: NotChecked → серая база (.lamp); Ok → зелёная (.on); Failed → красная (.fail).
    public bool IsOk => _state == ChainCheckState.Ok;
    public bool IsFailed => _state == ChainCheckState.Failed;

    public void SetState(ChainCheckState state) => State = state;

    public static ProviderStatusRow From(Services.Ai.IAiChatProvider provider) => provider switch
    {
        // Ступень lm-studio — декоратор: снимок берём с внутреннего провайдера
        // (после бутстрапа Model показывает фактически загруженную модель).
        Services.Ai.LmStudioBootstrappingProvider lms => new ProviderStatusRow
        {
            LinkId = lms.Name,
            Name = lms.Name,
            Model = lms.Inner.Model,
            BaseUrl = lms.Inner.BaseLabel,
            RequiresKey = false, // локальный: без ключа
            KeyPresent = true,
            InChain = true,
            KeyNote = "локальный, без ключа (подъём через lms при первом запросе)",
        },
        Services.Ai.OpenAiCompatibleProvider oc => new ProviderStatusRow
        {
            LinkId = oc.Name,
            Name = oc.Name,
            Model = oc.Model,
            BaseUrl = oc.BaseLabel,
            RequiresKey = !oc.IsLocal,
            KeyPresent = !oc.IsKeyMissing,
            InChain = true, // встроенные звенья включены по умолчанию (чекбокс)
            KeyNote = oc.IsKeyMissing
                ? "ключ НЕТ — введите в настройках"
                : oc.IsLocal ? "локальный, без ключа" : "ключ: токен",
        },
        Services.Ai.Llm7Provider l7 => new ProviderStatusRow
        {
            LinkId = l7.Name,
            Name = l7.Name,
            Model = l7.Model,
            BaseUrl = l7.BaseLabel,
            RequiresKey = false, // llm7 работает и анонимно
            KeyPresent = !l7.IsAnonymous,
            InChain = true,
            KeyNote = l7.IsAnonymous ? "анонимно (лимиты ниже)" : "ключ: токен",
        },
        _ => new ProviderStatusRow
        {
            LinkId = provider.Name,
            Name = provider.Name,
            Model = "—",
            KeyPresent = true,
            InChain = true,
            KeyNote = "готов",
        },
    };
}
