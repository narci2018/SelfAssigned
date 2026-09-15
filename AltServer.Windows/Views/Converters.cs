using System.Globalization;
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
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) // 绿
            : new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // 红
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