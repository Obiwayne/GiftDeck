using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The strip under the viewer count: "Starting OBS…", "Connecting to TikFinity…", or what went wrong.
// It stays while anything is loading or broken, and hides a few seconds after everything is ready.
public partial class LivePanel
{
    StatusRow _obsRow, _readerRow, _kickRow;
    DateTime? _allOkSince;
    const double HideAfterOkSeconds = 4;

    void StartStatusStrip()
    {
        _obsRow = new StatusRow(this);
        _readerRow = new StatusRow(this);
        StatusRows.Children.Add(_obsRow.Root);
        StatusRows.Children.Add(_readerRow.Root);
        _kickRow = new StatusRow(this);
        StatusRows.Children.Add(_kickRow.Root);
        Hub.Kick.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatusStrip);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateStatusStrip();
        timer.Start();
        Hub.Engine.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatusStrip);
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatusStrip);
        UpdateStatusStrip();
    }

    void UpdateStatusStrip()
    {
        var obs = StartupStatus.Obs();
        var reader = StartupStatus.Reader();
        _obsRow.Set(obs.kind, obs.text);
        _readerRow.Set(reader.kind, reader.text);
        var kick = StartupStatus.Kick();
        _kickRow.Set(kick.kind, kick.text);
        _kickRow.Root.Visibility = kick.kind == StatusKind.Off ? Visibility.Collapsed : Visibility.Visible;
        // OBS that GiftDeck doesn't run and isn't connected is the user's business, not a start-up step.
        _obsRow.Root.Visibility = obs.kind == StatusKind.Off ? Visibility.Collapsed : Visibility.Visible;

        bool busy = obs.kind is StatusKind.Loading or StatusKind.Error
                 || reader.kind is StatusKind.Loading or StatusKind.Error
                 || kick.kind is StatusKind.Loading or StatusKind.Error;
        if (busy) _allOkSince = null;
        else _allOkSince ??= DateTime.Now;
        bool show = busy || (DateTime.Now - _allOkSince.Value).TotalSeconds < HideAfterOkSeconds;
        StatusStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // One line: a spinner while loading, otherwise a coloured dot, then the words.
    sealed class StatusRow
    {
        public readonly Grid Root = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        readonly FrameworkElement _owner;
        readonly Ellipse _spinner, _dot;
        readonly TextBlock _text;

        public StatusRow(FrameworkElement owner)
        {
            _owner = owner;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var spin = new RotateTransform(0, 6, 6);
            _spinner = new Ellipse
            {
                Width = 12, Height = 12, StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 10, 6 },
                Stroke = (Brush)owner.FindResource("WarnBrush"),
                RenderTransform = spin,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
            };
            spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            _dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1.5, 4, 0, 0) };
            _text = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(_text, 1);
            Root.Children.Add(_spinner);
            Root.Children.Add(_dot);
            Root.Children.Add(_text);
        }

        public void Set(StatusKind kind, string text)
        {
            bool loading = kind == StatusKind.Loading;
            _spinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            _dot.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            _dot.Fill = Brush(kind switch { StatusKind.Ok => "SuccessBrush", StatusKind.Error => "DangerBrush", _ => "MutedBrush" });
            _text.Text = kind == StatusKind.Ok ? text + " ✓" : text;
            _text.Foreground = Brush(kind switch { StatusKind.Error => "DangerBrush", StatusKind.Loading => "TextBrush", _ => "MutedBrush" });
        }

        Brush Brush(string key) => (Brush)_owner.FindResource(key);
    }
}
