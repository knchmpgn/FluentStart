using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentStart;

/// <summary>
/// A CSS-style cubic-bezier easing (x1, y1, x2, y2), which is how Microsoft specifies Fluent motion curves.
/// WPF has no built-in cubic-bezier <see cref="IEasingFunction"/>, so this wraps <see cref="KeySpline"/>.
/// The CLR properties are settable so the curve can also be declared in XAML.
/// </summary>
public sealed class SplineEase : EasingFunctionBase
{
    private KeySpline? _spline;

    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; } = 1;
    public double Y2 { get; set; } = 1;

    public SplineEase() { EasingMode = EasingMode.EaseIn; }

    public SplineEase(double x1, double y1, double x2, double y2) : this()
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
    }

    protected override double EaseInCore(double normalizedTime)
    {
        _spline ??= new KeySpline(X1, Y1, X2, Y2);
        return _spline.GetSplineProgress(normalizedTime);
    }

    protected override Freezable CreateInstanceCore() => new SplineEase(X1, Y1, X2, Y2);
}

/// <summary>
/// Single source of truth for every transition in the app.
/// Durations and curves are the Fluent standard values from Microsoft's "Timing and easing" guidance:
/// ControlFasterAnimationDuration 83 ms, ControlFastAnimationDuration 167 ms, ControlNormalAnimationDuration 250 ms;
/// "Fast Out, Slow In" cubic-bezier(0, 0, 0, 1) for anything entering, "Slow Out, Fast In" cubic-bezier(1, 0, 1, 1) for anything leaving.
/// (These are the documented WinUI values; they are not measured from the native Windows 11 Start menu.)
/// </summary>
public static class Motion
{
    public static readonly Duration Faster = new(TimeSpan.FromMilliseconds(83));
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(167));
    public static readonly Duration Normal = new(TimeSpan.FromMilliseconds(250));

    public static readonly IEasingFunction Enter = Frozen(new SplineEase(0, 0, 0, 1));
    public static readonly IEasingFunction Exit = Frozen(new SplineEase(1, 0, 1, 1));

    /// <summary>Slide distances by scale of the surface that moves (DIPs).</summary>
    public const double SlideWindow = 28, SlidePage = 16, SlideItem = 8;

    /// <summary>False when Windows has "Animation effects" turned off: every transition then snaps to its end state.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static IEasingFunction Frozen(SplineEase e) { e.Freeze(); return e; }

    // One counter per (element, property): a newer run invalidates the completion callback of an older one.
    private sealed class Runs { public readonly Dictionary<DependencyProperty, int> Ids = new(); }
    private static readonly ConditionalWeakTable<DependencyObject, Runs> Table = new();

    private static int NextId(DependencyObject target, DependencyProperty dp)
    {
        var runs = Table.GetOrCreateValue(target);
        int id = runs.Ids.TryGetValue(dp, out int v) ? v + 1 : 1;
        runs.Ids[dp] = id;
        return id;
    }

    private static bool IsCurrent(DependencyObject target, DependencyProperty dp, int id) =>
        Table.TryGetValue(target, out var runs) && runs.Ids.TryGetValue(dp, out int cur) && cur == id;

    /// <summary>Stops any running transition on the property and leaves it at its current base value.</summary>
    public static void Cancel(DependencyObject target, DependencyProperty dp)
    {
        NextId(target, dp);
        (target as IAnimatable)?.BeginAnimation(dp, null);
    }

    /// <summary>Stops any running transition and snaps the property to <paramref name="value"/>.</summary>
    public static void Set(DependencyObject target, DependencyProperty dp, double value)
    {
        Cancel(target, dp);
        target.SetValue(dp, value);
    }

    /// <summary>
    /// Animates a double property to <paramref name="to"/>. Starts from <paramref name="from"/>, or from the value currently shown
    /// (so a transition interrupted halfway continues from where it is). <paramref name="done"/> runs only if no newer transition
    /// replaced this one.
    /// </summary>
    public static void Run(DependencyObject target, DependencyProperty dp, double to, Duration duration,
                           IEasingFunction? ease, double? from = null, Action? done = null)
    {
        int id = NextId(target, dp);
        var animatable = target as IAnimatable;
        double start = from ?? Convert.ToDouble(target.GetValue(dp));

        if (!Enabled || animatable == null)
        {
            animatable?.BeginAnimation(dp, null);
            target.SetValue(dp, to);
            done?.Invoke();
            return;
        }

        target.SetValue(dp, to);   // resting value once the animation ends (FillBehavior.Stop falls back to it)
        var anim = new DoubleAnimation(start, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) => { if (IsCurrent(target, dp, id)) done?.Invoke(); };
        animatable.BeginAnimation(dp, anim);
    }

    public static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } t) return t;
        var created = new TranslateTransform();
        element.RenderTransform = created;
        return created;
    }

    /// <summary>Standard entrance: fades in (Fast) while sliding vertically into place (Normal), both decelerating.</summary>
    public static void EnterY(UIElement element, double distance = SlidePage)
    {
        var t = EnsureTranslate(element);
        Run(element, UIElement.OpacityProperty, 1, Fast, Enter, from: 0);
        Run(t, TranslateTransform.YProperty, 0, Normal, Enter, from: distance);
    }

    /// <summary>Standard entrance sliding in horizontally.</summary>
    public static void EnterX(UIElement element, double distance = SlidePage)
    {
        var t = EnsureTranslate(element);
        Run(element, UIElement.OpacityProperty, 1, Fast, Enter, from: 0);
        Run(t, TranslateTransform.XProperty, 0, Normal, Enter, from: distance);
    }

    /// <summary>Standard exit for content that is being replaced: a short fade (Faster).</summary>
    public static void FadeOut(UIElement element, Action? done = null) =>
        Run(element, UIElement.OpacityProperty, 0, Faster, null, done: done);

    /// <summary>Standard fade in (Fast, decelerating) for content that does not move.</summary>
    public static void FadeIn(UIElement element, double from = 0) =>
        Run(element, UIElement.OpacityProperty, 1, Fast, Enter, from: from);
}
