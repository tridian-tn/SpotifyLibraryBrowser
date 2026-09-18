namespace SpotifyLibraryBrowser.Core.Browsing;

/// <summary>How the track list is ordered.</summary>
public enum TrackSort
{
    /// <summary>By album, then disc and track number - the order a record plays in.</summary>
    Album = 0,

    /// <summary>
    /// By a playlist's own running order, which only means anything while one playlist is selected.
    /// </summary>
    PlaylistOrder = 1
}
