using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace Glint.Windows;

// Keep scrolling in device-independent pixels and coalesce successive wheel input.
internal sealed class SmoothPageScroll
{
    private readonly ScrollViewer viewer;
    private readonly TranslateTransform feedback;
    private readonly Func<bool> animationsEnabled;
    private double target;
    private double position;
    private long lastFrame;
    private bool scrolling;

    internal SmoothPageScroll(ScrollViewer viewer, TranslateTransform feedback, Func<bool> animationsEnabled)
    {
        this.viewer = viewer;
        this.feedback = feedback;
        this.animationsEnabled = animationsEnabled;
        viewer.PreviewMouseWheel += OnMouseWheel;
        viewer.PreviewMouseDown += (_, _) => StopScroll();
        viewer.PreviewTouchDown += (_, _) => StopScroll();
        viewer.PreviewKeyDown += (_, _) => StopScroll();
        viewer.Unloaded += (_, _) => StopScroll();
        viewer.ManipulationBoundaryFeedback += (_, args) =>
        {
            if (!animationsEnabled() || viewer.ScrollableHeight <= 0) return;
            double vertical = args.BoundaryFeedback.Translation.Y;
            if (Math.Abs(vertical) < 0.1) return;
            Bounce(vertical > 0 ? 1 : -1);
            args.Handled = true;
        };
    }

    internal void Reset()
    {
        StopScroll();
        feedback.BeginAnimation(TranslateTransform.YProperty, null);
        target = 0;
    }

    private void StopScroll()
    {
        if (scrolling) CompositionTarget.Rendering -= OnRendering;
        scrolling = false;
        target = position = viewer.VerticalOffset;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!animationsEnabled() || IsNestedScrollInput(e.OriginalSource as DependencyObject))
        {
            StopScroll();
            feedback.BeginAnimation(TranslateTransform.YProperty, null);
            return;
        }

        int lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        double distance = lines < 0 ? viewer.ViewportHeight : lines * 16d;
        if (!scrolling) target = position = viewer.VerticalOffset;
        double delta = -e.Delta / 120d * distance;
        if (scrolling && Math.Sign(delta) != Math.Sign(target - position)) target = position;
        target = Math.Clamp(target + delta, 0, viewer.ScrollableHeight);
        e.Handled = true;

        if (Math.Abs(target - position) < 0.1)
        {
            StopScroll();
            if (viewer.ScrollableHeight > 0) Bounce(e.Delta > 0 ? 1 : -1);
            return;
        }

        feedback.BeginAnimation(TranslateTransform.YProperty, null);
        if (!scrolling)
        {
            scrolling = true;
            lastFrame = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Math.Clamp((now - lastFrame) / (double)Stopwatch.Frequency, 0, 0.05);
        lastFrame = now;
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        position += (target - position) * (1 - Math.Exp(-elapsed / 0.055));
        if (Math.Abs(target - position) < 0.25)
        {
            position = target;
            viewer.ScrollToVerticalOffset(position);
            CompositionTarget.Rendering -= OnRendering;
            scrolling = false;
            return;
        }
        viewer.ScrollToVerticalOffset(position);
    }

    private void Bounce(int direction)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(direction * 4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        feedback.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private bool IsNestedScrollInput(DependencyObject? node)
    {
        while (node != null && node != viewer)
        {
            if (node is ScrollViewer || node is ComboBox || node is Slider) return true;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
