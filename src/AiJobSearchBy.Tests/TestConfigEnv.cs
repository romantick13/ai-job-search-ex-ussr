using System.Runtime.CompilerServices;

namespace AiJobSearchBy.Tests;

// изоляция контейнерных тестов от профиля юзера — весь прогон
// пишет local.json только в temp-каталог (тесты не трогают
// %APPDATA%). Юнит-тесты ConfigService задают baseDirOverride явно; этот
// env-override — страховка для путей через CompositionRoot.Build.
// ModuleInitializer срабатывает один раз при загрузке тестовой сборки,
// существующие тесты правок не требуют.
internal static class TestConfigEnv
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable(
            "AIJOB_CONFIG_DIR",
            Path.Combine(Path.GetTempPath(), "aijsb-config-tests-run", Guid.NewGuid().ToString("N")));
    }
}
