using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Command;

namespace FloatingMind.Converters;

// === Blackboard类型 → 颜色 ===
public class BlackboardTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var type = value?.ToString() ?? "";
        return type switch
        {
            "Observation" => new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            "Fact" => new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
            "Hypothesis" => new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
            "Conflict" => new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
            "Decision" => new SolidColorBrush(Color.FromRgb(0xBA, 0x68, 0xC8)),
            _ => new SolidColorBrush(Colors.Gray)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// === 字符串 → 颜色（根据状态）===
public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value?.ToString() ?? "";
        return status switch
        {
            "Completed" or "Pass" => new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
            "Running" => new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            "Failed" or "Fail" => new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
            "Pending" => new SolidColorBrush(Colors.Gray),
            _ => new SolidColorBrush(Colors.Gray)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// === 风险等级 → 颜色 ===
public class RiskLevelToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is CommandRiskLevel level)
        {
            return level switch
            {
                CommandRiskLevel.L0_Auto => new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
                CommandRiskLevel.L1_Log => new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                CommandRiskLevel.L2_Confirm => new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
                CommandRiskLevel.L3_Forbidden => new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// === Bool → Visibility ===
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

// === InvertBool ===
public class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
