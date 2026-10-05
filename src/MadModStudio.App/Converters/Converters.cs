using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MadModStudio.Core.Models;
using MadModStudio.Core.Pipeline;
using MadModStudio.Game7DTD.Scanner;

namespace MadModStudio.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is not null && !(value is string s && s.Length == 0);
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>Maps severities, stage statuses, scan statuses and project statuses to theme brushes.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Error or StageStatus.Failed or ScanStatus.Broken or ProjectStatus.BuildFailed or ProjectStatus.ValidationFailed or ProjectStatus.PackagedWithErrors
            or IndexStatus.Failed or EacCompatibility.LikelyIncompatible => B("ErrorBrush"),
        Severity.Warning or StageStatus.Warning or ScanStatus.Warning or ScanStatus.ClientRequirementDetected or SideRequirement.ClientRequired
            or SideRequirement.Warning or IndexStatus.Stale or IndexStatus.Indexing => B("WarningBrush"),
        Severity.Pass or StageStatus.Succeeded or ScanStatus.Compatible or ProjectStatus.Packaged or ProjectStatus.BuildSucceeded
            or IndexStatus.Indexed or SideRequirement.LikelyServerSide or EacCompatibility.LikelyCompatible => B("SuccessBrush"),
        Severity.Info or StageStatus.Running => B("InfoBrush"),
        _ => B("TextDimBrush"),
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Human-friendly text for enum statuses (no fake certainty: "LIKELY" wording is preserved).</summary>
public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SideRequirement.LikelyServerSide => "LIKELY SERVER-SIDE",
        SideRequirement.ClientRequired => "CLIENT REQUIRED",
        SideRequirement.Warning => "WARNING",
        SideRequirement.Unknown => "UNKNOWN",
        EacCompatibility.LikelyCompatible => "LIKELY COMPATIBLE",
        EacCompatibility.LikelyIncompatible => "LIKELY INCOMPATIBLE",
        EacCompatibility.Unknown => "UNKNOWN",
        ModType.XmlOnly => "XML-only",
        ModType.HarmonyCSharp => "Harmony/C#",
        ModType.Hybrid => "Hybrid",
        ModType.Unknown => "Unknown",
        ScanStatus.ClientRequirementDetected => "Client Requirement Detected",
        ProjectStatus.PackagedWithErrors => "Packaged With Errors",
        ProjectStatus.BuildFailed => "Build Failed",
        ProjectStatus.ValidationFailed => "Validation Failed",
        ProjectStatus.BuildSucceeded => "Build Succeeded",
        StageStatus.Pending => "Pending",
        Severity s => s.ToString().ToUpperInvariant(),
        null => "",
        var o => o.ToString() ?? "",
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset d => d.LocalDateTime.ToString("g", culture),
        null => "never",
        var o => o.ToString() ?? "",
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class JoinConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is System.Collections.IEnumerable e and not string ? string.Join(parameter as string ?? ", ", e.Cast<object>()) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
