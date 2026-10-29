using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AltServer.Windows.Views;

/// <summary>布尔值 → 颜色 转换器（服务器运行状态指示）</summary>
public class RunningStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var running = value is true;
        return running
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)) // 绿
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)); // 红
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔值 → 文本 转换器（服务器状态文字）</summary>
public class ServerStateTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "运行中" : "已停止";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>空字符串/null → Collapsed（隐藏"绑定账号"等可空副标题）</summary>
public class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string;
        var prefix = parameter as string ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>剩余天数 → 颜色（>7 绿，1-7 橙，<=0 红）</summary>
public class DaysRemainingBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var days = value is int d ? d : 0;
        if (days <= 0)
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)); // 红
        if (days <= 7)
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06)); // 橙
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));     // 绿
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>导航项激活背景颜色转换器</summary>
public class NavActiveBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var current = value as string ?? string.Empty;
        var target = parameter as string ?? string.Empty;
        return string.Equals(current, target, StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDB, 0xEA, 0xFE)) // 浅蓝激活
            : System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>导航项激活字体颜色转换器</summary>
public class NavActiveForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var current = value as string ?? string.Empty;
        var target = parameter as string ?? string.Empty;
        return string.Equals(current, target, StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x4E, 0xD8)) // 深蓝文字
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x41, 0x55)); // 灰暗文字
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>视频指示颜色（视频 紫色，图片 灰色/蓝色）</summary>
public class BoolToVideoBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isVideo = value is true;
        return isVideo
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x93, 0x33, 0xEA)) // 紫色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)); // 蓝色
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}