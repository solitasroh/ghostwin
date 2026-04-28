using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace GhostWin.App.Animations;

/// <summary>
/// M-16-B FR-09 (Day 5): AnimationTimeline with GridLength interpolation.
/// WPF base does not provide this — required for animating ColumnDefinition.Width
/// (e.g. NotificationPanel toggle 200ms ease-out, cmux transition parity).
/// </summary>
/// <remarks>
/// Plan §5 R5 — verified pattern: store From/To/EasingFunction as DependencyProperties,
/// linear-interpolate the underlying double, and preserve the GridUnitType from
/// <see cref="To"/> so that Star vs Pixel targets do not get coerced.
/// </remarks>
public sealed class GridLengthAnimationCustom : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(
            nameof(From), typeof(GridLength), typeof(GridLengthAnimationCustom),
            new PropertyMetadata(GridLength.Auto));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(
            nameof(To), typeof(GridLength), typeof(GridLengthAnimationCustom),
            new PropertyMetadata(GridLength.Auto));

    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(
            nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimationCustom),
            new PropertyMetadata(null));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimationCustom();

    public override object GetCurrentValue(
        object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock is null) return From;

        var progress = animationClock.CurrentProgress ?? 0.0;
        if (EasingFunction is { } ease)
            progress = ease.Ease(progress);

        var fromVal = From.Value;
        var toVal = To.Value;
        var current = fromVal + ((toVal - fromVal) * progress);

        return new GridLength(current, To.GridUnitType);
    }
}
