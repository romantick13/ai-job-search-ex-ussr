using System.Text.Json;

namespace AiJobSearchBy.Services.Ai;

// ============================================================================
// WP-U2 (решение владельца 2026-10-22): сериализация векторных ссылок вынесена
// из JinaEmbeddingService в нейтральный хелпер — формат (компактный JSON-массив
// float) не зависит от модели-владельца вектора, а модель фиксируется отдельной
// колонкой VectorModel (PdfDocuments/Applications).
//
// FLOW: MainViewModel.SendAsync / PdfIndexerService -> VectorJson.To ->
//       база; ApplicationDetailsWindow -> VectorJson.From -> размерность.
// THREAD: без состояния.
// ============================================================================
public static class VectorJson
{
    // Компактный JSON-массив float — без внешних зависимостей.
    public static string To(IReadOnlyList<float> vector)
        => JsonSerializer.Serialize(vector);

    public static float[]? From(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<float[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
