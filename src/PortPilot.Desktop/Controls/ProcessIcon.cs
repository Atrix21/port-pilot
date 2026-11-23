using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PortPilot.Desktop.Services;

namespace PortPilot.Desktop.Controls;

/// <summary>
/// The application's own icon when the desktop knows one, otherwise a monogram tile.
/// </summary>
public sealed class ProcessIcon : Border
{
    public static readonly StyledProperty<string?> NameTextProperty =
        AvaloniaProperty.Register<ProcessIcon, string?>(nameof(NameText));

    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<ProcessIcon, string?>(nameof(Path));

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<ProcessIcon>();

    private readonly TextBlock _monogram = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeight.SemiBold,
    };

    private readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(2), IsVisible = false };
    private int _generation;

    public ProcessIcon()
    {
        Classes.Add("process-icon");
        Child = new Panel { Children = { _monogram, _image } };
    }

    public string? NameText
    {
        get => GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NameTextProperty || change.Property == PathProperty)
        {
            Update();
        }
        else if (change.Property == FontSizeProperty)
        {
            _monogram.FontSize = FontSize;
        }
    }

    private void Update()
    {
        var name = NameText ?? "";
        var letter = name.FirstOrDefault(char.IsLetterOrDigit);
        _monogram.Text = letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
        Show(null);

        var generation = ++_generation;
        var path = Path;
        Task.Run(() => DesktopIcons.Find(path, name)).ContinueWith(
            t =>
            {
                if (generation == _generation)
                {
                    Show(t.Result);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Show(Bitmap? bitmap)
    {
        _image.Source = bitmap;
        _image.IsVisible = bitmap is not null;
        _monogram.IsVisible = bitmap is null;
        Classes.Set("has-image", bitmap is not null);
    }
}
