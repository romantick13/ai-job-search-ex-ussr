using System;
using System.Threading;

namespace AiJobSearchExUssr.Tests;

// ============================================================================
// TestEnvGuard — канистра вокруг process-global переменных окружения
// в тестах (урок: падение Providers_ReflectChainState из-за чужого окна env).

// Что случилось: ConfigServiceTests.ResolveAiKey_Env_Wins ставит
// AIJOB_LLM7_API_KEY в своём окне try/finally; xUnit гоняет классы
// ПАРАЛЛЕЛЬНО, и конструктор Llm7Provider(apiKey: null) в соседнем классе
// словил чужое окно — карточка «ключ НЕТ» стала «ключ: токен», Assert.False
// упал. В песочнице гонка не легла (207/207), на другой машине легла —
// классическая флейкость «на моей машине проходит».

// Договор: все, кто трогает общую переменную (писатели и читатели), берут
// ОДНУ канистру — окна не пересекаются; внутри окна переменная гасится
// (реальный ключ машины в тест не утекает), Dispose возвращает как было.

// THREAD: SemaphoreSlim без thread-аффинити (Monitor сломался бы на
// продолжении await после рефакторинга); окна короткие (миллисекунды),
// синхронный Wait безопасен. Асинхронные тесты держат окно только вокруг
// конструктора — await наружу, заDispose.
// ============================================================================
public static class TestEnvGuard
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Взять окно: сохранить значения и погасить; using — вернуть и отпустить.
    public static IDisposable Scope(params string[] variables) => new ScopeImpl(variables);

    private sealed class ScopeImpl : IDisposable
    {
        private readonly string[] _variables;
        private readonly string?[] _saved;

        public ScopeImpl(string[] variables)
        {
            _variables = variables;
            Gate.Wait();
            _saved = new string?[variables.Length];
            for (var i = 0; i < variables.Length; i++)
            {
                _saved[i] = Environment.GetEnvironmentVariable(variables[i]);
                Environment.SetEnvironmentVariable(variables[i], null);
            }
        }

        public void Dispose()
        {
            try
            {
                for (var i = 0; i < _variables.Length; i++)
                    Environment.SetEnvironmentVariable(_variables[i], _saved[i]);
            }
            finally
            {
                Gate.Release(); // окно закрывается даже при упавшем ассерте
            }
        }
    }
}
