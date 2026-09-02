using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Clamav.Freshclam.Util.Options;

namespace Soenneker.Clamav.Freshclam.Util.Abstract;

/// <summary>
/// A cross-platform .NET utility for updating ClamAV virus definitions with bundled FreshClam runtimes.
/// </summary>
public interface IFreshclamUtil
{
    /// <summary>
    /// Downloads or incrementally updates the official ClamAV virus definitions.
    /// </summary>
    /// <param name="databaseDirectory">An optional writable database directory. The app-local default is used when omitted.</param>
    /// <param name="options">Optional FreshClam settings.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>Lines written by <c>freshclam</c>.</returns>
    ValueTask<IReadOnlyList<string>> Update(string? databaseDirectory = null, FreshclamOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a directory contains a supported ClamAV definition database.
    /// </summary>
    /// <param name="databaseDirectory">The definition directory to inspect.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns><see langword="true"/> when a supported definition file exists; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> HasDefinitions(string databaseDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the bundled FreshClam version string.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The first line written by <c>freshclam --version</c>.</returns>
    ValueTask<string> GetVersion(CancellationToken cancellationToken = default);
}
