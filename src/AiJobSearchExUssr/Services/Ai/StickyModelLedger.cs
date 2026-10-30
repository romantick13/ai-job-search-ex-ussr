using System;
using System.IO;
using System.Text.Json;
using AiJobSearchExUssr.Services;

namespace AiJobSearchExUssr.Services.Ai;

// ============================================================================
// StickyModelLedger — «липучесть» удачной модели агрегатора в файл.
// У агрегаторов (OpenRouter,
// OrcaRouter) перед запросом берётся список бесплатных моделей, выбирается
// наилучшая; при отказе — переход к следующей; УДАЧНАЯ модель запоминается
// в файл, чтобы следующие запросы начинались с неё без перебора каталога
// (экономия запросов и времени). Отказ залипшей модели сбрасывает запись —
// перебор начинается заново. Запись ротируется по суткам (как AiBudgetLedger):
// за ночь каталог агрегатора мог ротироваться — липучесть вчерашнего дня
// недействительна.

// Файл: <профиль>/ai-job-search-ex-ussr/state/<провайдер>-sticky.json
// {"date":"2026-10-25","model":"...","reason":"ok"}
// Любой сбой файла кричит и не роняет работу.
// ============================================================================
// LIFECYCLE: экземпляр на провайдера (синглтон DI, живёт в OpenAiCompatibleProvider)
// STATE: единственные мутации — Save/Clear под _gate; файл — персистентность
// THREAD: всё состояние под _gate
public sealed class StickyModelLedger
{
    private readonly object _gate = new();
    private readonly ILogService _log;
    private readonly string? _filePath;
    private bool _persistWarned;

    public string? Model { get; private set; }
    public DateTime Day { get; private set; }
    public string? Reason { get; private set; }

    // / <summary>Липучесть свежа: модель записана и запись сегодняшняя.</summary>
    public bool IsFresh
    {
        get { lock (_gate) { RollDay(); return !string.IsNullOrWhiteSpace(Model); } }
    }

    public StickyModelLedger(string? filePath, ILogService log)
    {
        _filePath = filePath;
        _log = log;
        Day = DateTime.Now.Date;
        Load();
    }

    // FLOW: успех провайдера -> Save(model, reason) -> файл перезаписан
    public void Save(string model, string reason)
    {
        lock (_gate)
        {
            RollDay();
            Model = model;
            Reason = reason;
            SaveFile();
        }
    }

    // / <summary>Отказ залипшей модели: запись стирается, перебор начнётся заново.</summary>
    public void Clear(string reason)
    {
        lock (_gate)
        {
            if (Model is null) return;
            _log.LogWarn($"skip: липучесть {Name()} сброшена — причина: {reason}; " +
                         "решение: перебор бесплатного каталога начнётся заново");
            Model = null;
            Reason = null;
            DeleteFile();
        }
    }

    private string Name() => Path.GetFileNameWithoutExtension(_filePath) ?? "sticky";

    private void RollDay()
    {
        var today = DateTime.Now.Date;
        if (today != Day)
        {
            Day = today;
            // Сутки сменились — вчерашняя липучесть недействительна (каталог мог ротироваться).
            if (Model is not null)
            {
                Model = null;
                Reason = null;
            }
        }
    }

    private void Load()
    {
        if (_filePath is null) return;
        try
        {
            if (!File.Exists(_filePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
            var root = doc.RootElement;
            var date = root.GetProperty("date").GetString();
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
            if (date == DateTime.Now.Date.ToString("yyyy-MM-dd") && !string.IsNullOrWhiteSpace(model))
            {
                Day = DateTime.Now.Date;
                Model = model;
                Reason = reason;
            }
        // Иначе файл вчерашний — начинаем без липучести (RollDay дал бы то же).
        }
        catch (Exception ex)
        {
            WarnPersist($"чтение липучести не удалось — {ex.Message}; решение: начинаю без липучести");
        }
    }

    private void SaveFile()
    {
        if (_filePath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(new
            {
                date = DateTime.Now.Date.ToString("yyyy-MM-dd"),
                model = Model,
                reason = Reason
            }));
        }
        catch (Exception ex)
        {
            WarnPersist($"запись липучести не удалась — {ex.Message}; решение: липучесть только в памяти до конца процесса");
        }
    }

    private void DeleteFile()
    {
        if (_filePath is null) return;
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch (Exception ex)
        {
            WarnPersist($"удаление файла липучести не удалось — {ex.Message}; решение: запись очищена в памяти");
        }
    }

    private void WarnPersist(string message)
    {
        if (_persistWarned) return; // один крик на процесс — дальше молча не персистентна
        _persistWarned = true;
        _log.LogWarn($"skip: липучесть модели не персистентна — {message}");
    }
}
