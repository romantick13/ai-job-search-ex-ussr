using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AiJobSearchExUssr.Controls;

// ============================================================================

// единый хелпер — Esc закрывает модалку при любом фокусе внутри неё.
// Туннелирование от окна вниз: даже когда фокус в TextBox, Esc доходит.
// Подключается одной строкой в ctor каждого диалогового окна и к
// code-behind-модалкам MainWindow (канон, первый запуск, lm-studio).
// STATE: без состояния. THREAD: UI-поток.
// ============================================================================
public static class ModalClose
{
    public static void Attach(Window window)
        => window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            window.Close();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
}
