using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SpotifyLibraryBrowser.Core.Browsing;

namespace SpotifyLibraryBrowser.App.ViewModels;

/// <summary>
/// One row in the track list. Liked state lives here rather than on the immutable row so the
/// heart can flip the moment it's clicked, before the API has answered.
/// </summary>
/// <param name="row">The track as read from the index</param>
public sealed partial class TrackViewModel(TrackRow row) : ObservableObject
{
    [ObservableProperty]
    private bool _isLiked = row.IsLiked;

    /// <summary>Whether this track's album is itself in the library.</summary>
    [ObservableProperty]
    private bool _albumIsSaved = row.AlbumIsSaved;

    /// <summary>The underlying row.</summary>
    public TrackRow Row { get; } = row;

    /// <summary>The Spotify track ID.</summary>
    public string Id => Row.Id;

    /// <summary>The Spotify track URI, for playback and library writes.</summary>
    public string Uri => Row.Uri;

    /// <summary>The track title.</summary>
    public string Name => Row.Name;

    /// <summary>The credited artists, in credit order.</summary>
    public string ArtistNames => Row.ArtistNames;

    /// <summary>The album title.</summary>
    public string AlbumName => Row.AlbumName;

    /// <summary>The album's Spotify ID.</summary>
    public string AlbumId => Row.AlbumId;

    /// <summary>The album's Spotify URI, used to play the track in album context.</summary>
    public string AlbumUri => $"spotify:album:{Row.AlbumId}";

    /// <summary>The track's index within its album, used as the playback offset.</summary>
    public int TrackNumber => Row.TrackNumber;

    /// <summary>The release year, blank when the album has no usable date.</summary>
    public string Year => Row.Year ?? string.Empty;

    /// <summary>The track length as minutes and seconds.</summary>
    public string Duration => TimeSpan.FromMilliseconds(Row.DurationMs).ToString(@"m\:ss");
}
