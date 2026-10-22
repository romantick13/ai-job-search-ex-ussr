using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

// ВЕРБАТИМ-ПОРТ из донора UniD (upload/UniD.zip -> UniD/Controls/SparklingProgressBar.cs,
// исходники получены от владельца 2026-10-21; решение владельца «слямзишь оттуда
// контролы, можно сделать хардкодом»): изменён только namespace
// UniD.Controls -> AiJobSearchBy.Controls, тело не тронуто. Ключи скина
// ProgressBar.GradientStart/End у нас не заданы — контрол честно работает
// с дефолтами; TrailEndBrush задаётся в MainWindow.axaml (WP-U0).
namespace AiJobSearchBy.Controls;

/// <summary>
/// ProgressBar с системой частиц (искры "бенгальского огня") на конце
/// заполненной полосы — перенесено из проекта Button (Controls/NeuSparklingProgressBar.cs,
/// см. итерации 1-6 в комментариях там же) как реальный потребитель ключей
/// скина, которые Button уже экспортирует, но которые раньше ничего не
/// красили в UniD: "ProgressBar.GradientStart", "ProgressBar.GradientEnd",
/// "ProgressBar.SparkEffect".
///
/// Два отличия от версии в Button (там это была демонстрационная полоса,
/// здесь — рабочий контрол в реальном списке загрузок):
///
/// 1. Заливка (PART_Indicator.Background) больше не задаётся статикой из
///    XAML-темы — она собирается в коде из двух StyledProperty (см.
///    UpdateIndicatorGradient) в LinearGradientBrush. Foreground используется
///    как цвет начала (совместимо с тем, что MainWindow.axaml и раньше
///    выставлял Foreground="{DynamicResource ProgressBar.Foreground}" —
///    старые скины без ProgressBar.GradientStart/End продолжат работать:
///    если TrailEndBrush не задан, оба конца градиента совпадают и полоса
///    выглядит как раньше, сплошной заливкой).
/// 2. Добавлено SparklesEnabled (bool StyledProperty) — искры и свечение
///    спаунятся/рисуются только если true. Привязывается к
///    "ProgressBar.SparkEffect" в стиле, см. MainWindow.axaml. Скины без
///    этого ключа получают default = true (эффект виден "из коробки"), а
///    не молча ломаются.
/// </summary>
public class SparklingProgressBar : ProgressBar
{
    public static readonly StyledProperty<IBrush?> TrailEndBrushProperty =
        AvaloniaProperty.Register<SparklingProgressBar, IBrush?>(nameof(TrailEndBrush));

    public IBrush? TrailEndBrush
    {
        get => GetValue(TrailEndBrushProperty);
        set => SetValue(TrailEndBrushProperty, value);
    }

    public static readonly StyledProperty<bool> SparklesEnabledProperty =
        AvaloniaProperty.Register<SparklingProgressBar, bool>(nameof(SparklesEnabled), true);

    public bool SparklesEnabled
    {
        get => GetValue(SparklesEnabledProperty);
        set => SetValue(SparklesEnabledProperty, value);
    }

    private class Spark
    {
        public Point Position;
        public Point Velocity;
        public double Lifetime;
        public double MaxLifetime;
        public double Size;
        public double WobbleFreq;
        public double WobbleAmp;
        public double WobblePhase;
    }

    private readonly List<Spark> _sparks = new();
    private readonly Random _random = new();
    private DispatcherTimer? _renderTimer;
    private DateTime _lastUpdate;
    private Border? _indicator;

    private double _burnX;
    private double _burnY;
    private bool _isBurning;
    private double _glowPhase;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _indicator = e.NameScope.Find<Border>("PART_Indicator");
        UpdateIndicatorGradient();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ForegroundProperty || change.Property == TrailEndBrushProperty)
        {
            UpdateIndicatorGradient();
        }
    }

    /// <summary>
    /// Собирает LinearGradientBrush заливки из Foreground (начало, = скин-ключ
    /// "ProgressBar.GradientStart" через стиль) и TrailEndBrush (конец, =
    /// "ProgressBar.GradientEnd"). Если TrailEndBrush не задан (скин без этого
    /// ключа) — оба конца совпадают, получается сплошная заливка, как раньше.
    /// </summary>
    private void UpdateIndicatorGradient()
    {
        if (_indicator == null)
        {
            return;
        }

        var startColor = (Foreground as ISolidColorBrush)?.Color ?? Colors.Orange;
        var endColor = (TrailEndBrush as ISolidColorBrush)?.Color ?? startColor;

        _indicator.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(startColor, 0),
                new GradientStop(endColor, 1)
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _lastUpdate = DateTime.Now;
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += OnTick;
        _renderTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_renderTimer != null)
        {
            _renderTimer.Tick -= OnTick;
            _renderTimer.Stop();
            _renderTimer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastUpdate).TotalSeconds;
        _lastUpdate = now;

        var range = Maximum - Minimum;
        var percent = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        _burnX = Bounds.Width * percent;
        _burnY = Bounds.Height / 2;

        _isBurning = SparklesEnabled && Value > Minimum && Value < Maximum;

        if (_isBurning)
        {
            SpawnSparks(_burnX, _burnY, count: 4);

            _glowPhase += elapsed * 5.0;
            if (_glowPhase > Math.PI * 2)
            {
                _glowPhase -= Math.PI * 2;
            }
        }

        UpdateSparks(elapsed);
        InvalidateVisual();
    }

    private void SpawnSparks(double x, double y, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var speed = _random.NextDouble() * 150 + 50;
            var start = new Point(x, y);

            var spark = new Spark
            {
                Position = start,
                Velocity = new Point(Math.Cos(angle) * speed, Math.Sin(angle) * speed),
                MaxLifetime = _random.NextDouble() * 0.4 + 0.2,
                Lifetime = 0,
                Size = _random.NextDouble() * 2 + 1,
                WobbleFreq = _random.NextDouble() * 18 + 14,
                WobbleAmp = _random.NextDouble() * 30 + 20,
                WobblePhase = _random.NextDouble() * Math.PI * 2
            };

            _sparks.Add(spark);
        }
    }

    private void UpdateSparks(double elapsed)
    {
        const double gravity = 120;

        for (var i = _sparks.Count - 1; i >= 0; i--)
        {
            var spark = _sparks[i];
            spark.Lifetime += elapsed;

            if (spark.Lifetime >= spark.MaxLifetime)
            {
                _sparks.RemoveAt(i);
                continue;
            }

            spark.WobblePhase += spark.WobbleFreq * elapsed;

            var speed = Math.Sqrt(spark.Velocity.X * spark.Velocity.X + spark.Velocity.Y * spark.Velocity.Y);
            var vx = spark.Velocity.X;
            var vy = spark.Velocity.Y + gravity * elapsed;

            if (speed > 0.01)
            {
                var perpX = -spark.Velocity.Y / speed;
                var perpY = spark.Velocity.X / speed;
                var wobble = Math.Sin(spark.WobblePhase) * spark.WobbleAmp * elapsed;

                vx += perpX * wobble;
                vy += perpY * wobble;
            }

            spark.Velocity = new Point(vx, vy);
            spark.Position = new Point(
                spark.Position.X + spark.Velocity.X * elapsed,
                spark.Position.Y + spark.Velocity.Y * elapsed);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_isBurning)
        {
            DrawGlow(context);
        }

        foreach (var spark in _sparks)
        {
            DrawSparkTrail(context, spark);
        }
    }

    private static void DrawSparkTrail(DrawingContext context, Spark spark)
    {
        var speed = Math.Sqrt(spark.Velocity.X * spark.Velocity.X + spark.Velocity.Y * spark.Velocity.Y);

        if (speed < 0.1)
        {
            return;
        }

        var dirX = spark.Velocity.X / speed;
        var dirY = spark.Velocity.Y / speed;

        var tailLength = speed * 0.08;
        var tail = new Point(
            spark.Position.X - dirX * tailLength,
            spark.Position.Y - dirY * tailLength);

        var color = GetSparkColor(spark);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(spark.Position, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(tail, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };

        var lifeRatio = spark.Lifetime / spark.MaxLifetime;
        var currentSize = spark.Size * (1.0 - lifeRatio * 0.3);

        var pen = new Pen(brush, currentSize, lineCap: PenLineCap.Flat);
        context.DrawLine(pen, spark.Position, tail);

        var coreBrush = new SolidColorBrush(color);
        context.DrawEllipse(coreBrush, null, spark.Position, currentSize * 0.6, currentSize * 0.6);
    }

    private static Color GetSparkColor(Spark spark)
    {
        var lifeRatio = spark.Lifetime / spark.MaxLifetime;

        if (lifeRatio < 0.1)
        {
            return Colors.White;
        }

        if (lifeRatio < 0.3)
        {
            return Color.FromArgb(255, 255, 200, 0);
        }

        var fadeRatio = (lifeRatio - 0.3) / 0.7;
        var alpha = (byte)(255 * (1 - Math.Pow(fadeRatio, 2)));
        var green = (byte)(160 * (1 - fadeRatio) + 40);

        return Color.FromArgb(alpha, 255, green, 0);
    }

    private void DrawGlow(DrawingContext context)
    {
        var center = new Point(_burnX, _burnY);
        var pulse = 1.0 + Math.Sin(_glowPhase) * 0.15;

        DrawGlowLayer(context, center, 12 * pulse,
            Color.FromArgb(100, 255, 120, 0),
            Colors.Transparent);

        DrawGlowLayer(context, center, 6 * pulse,
            Color.FromArgb(180, 255, 200, 0),
            Colors.Transparent);

        var coreBrush = new SolidColorBrush(Color.FromArgb(255, 255, 240, 200));
        context.DrawEllipse(coreBrush, null, center, 2.5 * pulse, 2.5 * pulse);
    }

    private static void DrawGlowLayer(DrawingContext context, Point center, double radius, Color innerColor, Color outerColor)
    {
        if (radius <= 0)
        {
            return;
        }

        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(innerColor, 0),
                new GradientStop(outerColor, 1)
            }
        };

        context.DrawEllipse(brush, null, center, radius, radius);
    }
}
