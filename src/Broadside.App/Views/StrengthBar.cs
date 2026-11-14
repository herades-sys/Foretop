using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Broadside.App.Views;

/// <summary>Two-colour bar: friendly share on the left, enemy on the right. Grey when there is no estimate.</summary>
public sealed class StrengthBar : FrameworkElement
{
    public static readonly DependencyProperty ShareProperty =
        DependencyProperty.Register(nameof(Share), typeof(double?), typeof(StrengthBar),
            new PropertyMetadata(null, (d, e) => ((StrengthBar)d).Animate((double?)e.NewValue)));

    private static readonly DependencyProperty DisplayShareProperty =
        DependencyProperty.Register("DisplayShare", typeof(double), typeof(StrengthBar),
            new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Share { get => (double?)GetValue(ShareProperty); set => SetValue(ShareProperty, value); }

    private void Animate(double? value)
    {
        var target = Math.Clamp(value ?? 0.5, 0, 1);
        BeginAnimation(DisplayShareProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        if (Share is null)
        {
            dc.DrawRoundedRectangle((Brush)FindResource("Border"), null, new Rect(0, 0, w, h), r, r);
            return;
        }
        var split = w * (double)GetValue(DisplayShareProperty);
        dc.DrawRoundedRectangle((Brush)FindResource("Enemy"), null, new Rect(0, 0, w, h), r, r);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, split, h)));
        dc.DrawRoundedRectangle((Brush)FindResource("Friendly"), null, new Rect(0, 0, w, h), r, r);
        dc.Pop();
        dc.DrawRectangle(Brushes.White, null, new Rect(w / 2 - 0.5, 0, 1, h));
    }
}
