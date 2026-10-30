using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AiJobSearchExUssr.Controls;

// РОЛЬ: краска строк живого протокола по маркеру уровня события.
// THREAD: создание кистей — только UI-поток; состояния у конвертера нет.
// Палитра (семантика — цвет уровня):
// без маркера -> БЕЛЫЙ текст (обычные строки читаются всегда; тема
// через наследование давала полусерый — нечитаемо)
// птица ✅/✓ -> ЗЕЛЁНЫЙ #4CC38A — удача
// ошибка ❌/✗ -> КРАСНЫЙ #D64550 — неудача
// skip/⚠ -> ЖЁЛТЫЙ #FFCC4D — осознанный пропуск
// Режимы (ConverterParameter):
// «bg» -> фон строки: полупрозрачная подложка того же цвета — строка
// подсвечена целиком, тёмная тема не слепнет; обычным строкам
// подложки нет (UnsetValue).
// прочее/нет -> цвет текста: плотные цвета маркеров.
// Слоты цветов крутятся здесь, XAML не трогаем.
// Escapes \u2705/\u274C/\u26A0 — по конвенции ILogService (независимость
// исходника от BOM и кодовой страницы инструментов).
public class LogLineBrushConverter : IValueConverter
{
    private static readonly IBrush PlainBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#4CC38A"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D64550"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFCC4D"));

    // Подложки для режима «bg» (первый байт — альфа: заметно, но не слепит).
    private static readonly IBrush SuccessBack = new SolidColorBrush(Color.Parse("#384CC38A"));
    private static readonly IBrush ErrorBack = new SolidColorBrush(Color.Parse("#38D64550"));
    private static readonly IBrush WarnBack = new SolidColorBrush(Color.Parse("#38FFCC4D"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string line)
            return AvaloniaProperty.UnsetValue;

        var asBackground = parameter is string p && p == "bg";

        // Порядок проверок: ошибка — первой (в смеси символов красим как ошибку).
        if (line.Contains('\u274C') || line.Contains('\u2717'))   // ❌ / ✗
            return asBackground ? ErrorBack : ErrorBrush;

        if (line.Contains('\u2705') || line.Contains('\u2713'))   // ✅ / ✓ (птица)
            return asBackground ? SuccessBack : SuccessBrush;

        if (line.Contains('\u26A0') || line.Contains("skip:"))    // ⚠ / «Δ skip: …»
            return asBackground ? WarnBack : WarnBrush;

        // Обычная строка: текст — белый всегда (наследование темы давало
        // полусерый, нечитаемо); подложки нет.
        return asBackground ? AvaloniaProperty.UnsetValue : PlainBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue; // обратная конвертация не используется
}
