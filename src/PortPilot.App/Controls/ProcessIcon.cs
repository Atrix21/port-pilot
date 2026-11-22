using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PortPilot.App.Native;

namespace PortPilot.App.Controls;

/// <summary>
/// Shows the real icon of an executable, loaded off the UI thread and cached by path. Until the icon
/// arrives – or when there is no accessible executable – a quiet monogram tile is shown instead.
/// </summary>
public sealed class ProcessIcon : Control
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(ProcessIcon), new PropertyMetadata(null, (d, _) => ((ProcessIcon)d).Load()));

    public static readonly DependencyProperty NameTextProperty = DependencyProperty.Register(
        nameof(NameText), typeof(string), typeof(ProcessIcon), new PropertyMetadata(null, (d, _) => ((ProcessIcon)d).UpdateMonogram()));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ProcessIcon), new PropertyMetadata(null));

    public static readonly DependencyProperty MonogramProperty = DependencyProperty.Register(
        nameof(Monogram), typeof(string), typeof(ProcessIcon), new PropertyMetadata(""));

    static ProcessIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(ProcessIcon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(ProcessIcon), new UIPropertyMetadata(false));
    }

    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public string? NameText
    {
        get => (string?)GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        private set => SetValue(SourceProperty, value);
    }

    public string Monogram
    {
        get => (string)GetValue(MonogramProperty);
        private set => SetValue(MonogramProperty, value);
    }

    private void UpdateMonogram()
    {
        var name = NameText;
        Monogram = string.IsNullOrEmpty(name) ? "?" : char.ToUpper(name[0], CultureInfo.CurrentCulture).ToString();
    }

    private async void Load()
    {
        var path = Path;
        if (string.IsNullOrEmpty(path))
        {
            Source = null;
            return;
        }

        var task = Cache.GetOrAdd(path, p => Task.Run<ImageSource?>(() => Extract(p)));
        if (task.IsCompletedSuccessfully)
        {
            Source = task.Result;
            return;
        }

        Source = null;
        var image = await task;
        if (Path == path)
        {
            Source = image;
        }
    }

    private static BitmapSource? Extract(string path)
    {
        // 48px renders crisply at 20px on 200% displays and at 40px on 100–125% displays.
        const int size = 48;
        if (Win32.SHDefExtractIcon(path, 0, 0, out var large, out var small, size | (16 << 16)) != 0 || large == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Win32.DestroyIcon(large);
            if (small != 0)
            {
                Win32.DestroyIcon(small);
            }
        }
    }
}
