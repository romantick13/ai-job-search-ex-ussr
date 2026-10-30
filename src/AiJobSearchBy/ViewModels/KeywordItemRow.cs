using CommunityToolkit.Mvvm.ComponentModel;

namespace AiJobSearchBy.ViewModels;

// строка каталога ключевых слов (скролл-список с чекбоксами —
// «оставь список со скроллингом с чекбоксами»).
// STATE: IsChecked — выбрано ли для активного профиля; NewInDict — слово
// только что добавлено в словарь (подсветка «+»); UsedCount — сколько
// вакансий прошлого поиска содержали слово (ориентир полезности).
public sealed class KeywordItemRow : ObservableObject
{
    private bool _isChecked;
    private bool _isMatched;

    public required string Value { get; init; }
    public required string Category { get; init; }
    public int UsedCount { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    // Слово найдено в активном резюме (галочка «в наборе» при экстракции).
    public bool IsMatched
    {
        get => _isMatched;
        set => SetProperty(ref _isMatched, value);
    }

    public string CountDisplay => UsedCount > 0 ? UsedCount.ToString() : "";
}
