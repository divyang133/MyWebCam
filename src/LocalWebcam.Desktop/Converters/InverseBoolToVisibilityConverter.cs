using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace LocalWebcam.Desktop.Converters;

/// <summary>The inverse of <see cref="BoolToVisibilityConverter"/> - used to show/hide the two halves of a toggle button's icon based on the same bool.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
