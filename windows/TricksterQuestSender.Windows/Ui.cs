using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TricksterQuestSender.Core;

namespace QuestSenderWin;

public sealed record KindOption(StepKind Value, string Label);

public static class Ui
{
    public static IReadOnlyList<KindOption> KindOptions { get; } =
        Enum.GetValues<StepKind>().Select(k => new KindOption(k, StepKinds.Label(k))).ToList();

    public static IReadOnlyList<string> Keys => KeyNames.All;
}

/// <summary>Visible when the step kind is one of the comma-separated names in the parameter.</summary>
public sealed class KindVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var names = (parameter as string ?? "").Split(',', StringSplitOptions.TrimEntries);
        return value is StepKind k && names.Contains(k.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? $"จำไว้ {s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} หน้า" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NonEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
