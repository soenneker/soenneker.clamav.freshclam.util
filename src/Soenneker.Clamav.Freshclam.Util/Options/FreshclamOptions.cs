using System;

namespace Soenneker.Clamav.Freshclam.Util.Options;

/// <summary>
/// Configures a FreshClam definition update.
/// </summary>
public sealed class FreshclamOptions
{
    /// <summary>
    /// Gets or sets the ClamAV database mirror hostname.
    /// </summary>
    public string DatabaseMirror { get; set; } = "database.clamav.net";

    /// <summary>
    /// Gets or sets the number of update checks per day written to the FreshClam configuration. Valid values are 1 through 50.
    /// </summary>
    public int ChecksPerDay { get; set; } = 12;

    /// <summary>
    /// Gets or sets the maximum update duration. A null value allows FreshClam to run until completion or cancellation.
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}
