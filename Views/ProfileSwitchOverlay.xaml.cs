using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GiftDeck.Views;

// The profile-switch animation: the bolt flies in to the parcel, shoots off, the parcel spins,
// then the bolt slams back into the middle. The switch itself runs while the bolt is resting.
public partial class ProfileSwitchOverlay : UserControl
{
    public bool Busy { get; private set; }

    public ProfileSwitchOverlay() => InitializeComponent();

    public void Play(string profile, Func<string> doSwitch, Action done)
    {
        if (Busy) return;
        Busy = true;
        Caption.Text = $"Loading {profile}";
        SubCaption.Text = "events, overlays, title and category";
        Visibility = Visibility.Visible;

        // One keyframe timeline per moving part, addressed through the element that owns it.
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var easeInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var bounce = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };

        // keys: (seconds, value, easing into this key; null = straight line, "jump" = instant)
        void Track(UIElement target, string path, params (double t, double v, object ease)[] keys)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            foreach (var (t, v, ease) in keys)
            {
                var time = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t));
                if (ease is string) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(v, time));
                else a.KeyFrames.Add(new EasingDoubleKeyFrame(v, time, ease as IEasingFunction));
            }
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, new PropertyPath(path));
            sb.Children.Add(a);
        }
        const string scaleX = "(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)";
        const string scaleY = "(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)";
        const string moveX = "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)";
        const string moveY = "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)";
        const string spin = "(UIElement.RenderTransform).(RotateTransform.Angle)";
        const string jump = "jump";

        // dim in, hold, fade out
        Track(Root, "Opacity", (0, 0, null), (0.15, 1, null), (2.15, 1, null), (2.40, 0, null));
        // 1. bolt flies in from the top right  2. shoots back off  4. slams back into the middle
        Track(BoltCanvas, "Opacity", (0, 0, null), (0.15, 0, null), (0.35, 1, null), (0.66, 1, null), (0.84, 0, null), (1.40, 0, null), (1.52, 1, null), (2.40, 1, null));
        Track(BoltCanvas, moveX, (0, 70, null), (0.15, 70, null), (0.45, 0, easeOut), (0.62, 0, null), (0.84, -25, easeIn), (1.38, -25, null), (1.39, 0, jump), (2.40, 0, null));
        Track(BoltCanvas, moveY, (0, -70, null), (0.15, -70, null), (0.45, 0, easeOut), (0.62, 0, null), (0.84, -120, easeIn), (1.38, -120, null), (1.39, 0, jump), (2.40, 0, null));
        Track(BoltCanvas, scaleX, (0, 0.6, null), (0.15, 0.6, null), (0.45, 1, easeOut), (1.39, 1, null), (1.40, 2.2, jump), (1.80, 1, bounce), (2.40, 1, null));
        Track(BoltCanvas, scaleY, (0, 0.6, null), (0.15, 0.6, null), (0.45, 1, easeOut), (1.39, 1, null), (1.40, 2.2, jump), (1.80, 1, bounce), (2.40, 1, null));
        // 3. parcel spins round
        Track(Parcel, spin, (0, 0, null), (0.80, 0, null), (1.40, 360, easeInOut), (2.40, 360, null));

        sb.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Busy = false;
            done?.Invoke();
        };

        // Do the switch while the bolt rests on the parcel (a brief pause there isn't noticeable).
        string summary = null;
        bool failed = false;
        var at = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.47) };
        at.Tick += (_, _) =>
        {
            at.Stop();
            try { summary = doSwitch(); }
            catch (Exception ex) { summary = ex.Message; failed = true; }
        };
        // "ready" when the bolt lands back in the middle
        var landed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.45) };
        landed.Tick += (_, _) =>
        {
            landed.Stop();
            Caption.Text = failed ? $"Couldn't load {profile}" : $"{profile} ready ✓";
            Caption.Foreground = (System.Windows.Media.Brush)FindResource(failed ? "DangerBrush" : "TextBrush");
            SubCaption.Text = summary ?? "";
        };

        sb.Begin();
        at.Start();
        landed.Start();
    }
}
