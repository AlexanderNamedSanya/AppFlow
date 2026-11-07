using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppFlow.Core;
using AppFlow.Desktop.ViewModels;

namespace AppFlow.Desktop.Services;

public static class ActionAnimation
{
    public static readonly DependencyProperty SequenceProperty = DependencyProperty.RegisterAttached(
        "Sequence", typeof(long), typeof(ActionAnimation), new PropertyMetadata(0L, OnSequenceChanged));
    public static long GetSequence(DependencyObject element) => (long)element.GetValue(SequenceProperty);
    public static void SetSequence(DependencyObject element, long value) => element.SetValue(SequenceProperty, value);

    private static void OnSequenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if ((long)e.NewValue == 0 || sender is not FrameworkElement element || !element.IsVisible
            || !SystemParameters.ClientAreaAnimation || element.DataContext is not TimelineNode node) return;
        var closing = node.LastAction == FlowAction.Close;
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = scale;
        var motion = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(closing ? .75 : 1.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(closing ? .25 : .6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
