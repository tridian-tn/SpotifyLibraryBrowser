using System;
using System.Globalization;
using Avalonia.Data.Converters;
using SpotifyAPI.Web;

namespace SpotifyLibraryBrowser.App.ViewModels;

/// <summary>A Connect device as shown in the device picker.</summary>
/// <param name="device">The device Spotify reported</param>
public sealed class DeviceRow(Device device)
{
    /// <summary>The device's Spotify ID.</summary>
    public string? Id => device.Id;

    /// <summary>Whether this is the device currently playing.</summary>
    public bool IsActive => device.IsActive;

    /// <summary>The device name, with its kind alongside so identical names stay tellable apart.</summary>
    public string Label => string.IsNullOrEmpty(device.Type)
        ? device.Name
        : $"{device.Name} ({device.Type})";
}

/// <summary>Turns liked state into the heart glyph shown in the track list.</summary>
public sealed class HeartConverter : IValueConverter
{
    /// <summary>The shared instance, referenced straight from XAML.</summary>
    public static readonly HeartConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "♥" : "♡";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
