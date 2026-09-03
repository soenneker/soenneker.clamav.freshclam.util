using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Asyncs.Locks;
using Soenneker.Clamav.Freshclam.Util.Abstract;
using Soenneker.Clamav.Freshclam.Util.Options;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Path.Abstract;
using Soenneker.Utils.Paths.Resources.Abstract;
using Soenneker.Utils.PooledStringBuilders;
using Soenneker.Utils.Process.Abstract;
using Soenneker.Utils.Runtime;

namespace Soenneker.Clamav.Freshclam.Util;

public sealed class FreshclamUtil : IFreshclamUtil
{
    private static readonly AsyncLock _updateLock = new();
    private static readonly string[] _definitionExtensions = ["cvd", "cld", "cud", "ndb"];
    private static readonly string[] _requiredDatabases = ["main", "daily", "bytecode"];

    private readonly IProcessUtil _processUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IPathUtil _pathUtil;
    private readonly IResourcesPathUtil _resourcesPathUtil;
    private readonly ILogger<FreshclamUtil> _logger;
    private readonly bool _windows;
    private readonly string _runtimeIdentifier;

    public FreshclamUtil(IProcessUtil processUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IPathUtil pathUtil,
        IResourcesPathUtil resourcesPathUtil, ILogger<FreshclamUtil> logger)
    {
        _processUtil = processUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _pathUtil = pathUtil;
        _resourcesPathUtil = resourcesPathUtil;
        _logger = logger;

        EnsureSupportedPlatform();

        _windows = RuntimeUtil.IsWindows();
        _runtimeIdentifier = _windows ? "win-x64" : "linux-x64";
        _logger.LogDebug("Initialized FreshClam for {RuntimeIdentifier}", _runtimeIdentifier);
    }

    public async ValueTask<IReadOnlyList<string>> Update(string? databaseDirectory = null, FreshclamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FreshclamOptions();
        Validate(options);
        (string runtimeDirectory, string freshclamPath) = await GetRuntimePaths(cancellationToken).NoSync();
        await EnsureToolExists(freshclamPath, cancellationToken).NoSync();
        EnsureExecutable(freshclamPath);

        string fullDatabaseDirectory = string.IsNullOrWhiteSpace(databaseDirectory)
            ? await _resourcesPathUtil.GetResourceFilePath("clamav-database", cancellationToken).NoSync()
            : Path.GetFullPath(databaseDirectory);
        await _directoryUtil.Create(fullDatabaseDirectory, log: false, cancellationToken).NoSync();
        string configurationPath = await _pathUtil.GetRandomTempFilePath(".conf", cancellationToken).NoSync();

        try
        {
            _logger.LogInformation("Updating ClamAV definitions in {DatabaseDirectory}", fullDatabaseDirectory);
            _logger.LogDebug("Waiting for the FreshClam update lock");

            using (await _updateLock.Lock(cancellationToken).NoSync())
            {
                _logger.LogDebug("Acquired the FreshClam update lock");
                await _fileUtil.Write(configurationPath, BuildConfiguration(options), log: false, cancellationToken).NoSync();

                string arguments = $"--config-file={Quote(configurationPath)} --datadir={Quote(fullDatabaseDirectory)} --stdout";
                List<string> output = await _processUtil.Start(freshclamPath, runtimeDirectory, arguments, timeout: options.Timeout, log: false,
                    environmentalVars: BuildEnvironment(runtimeDirectory), cancellationToken: cancellationToken).NoSync();

                await ValidateDatabases(fullDatabaseDirectory, cancellationToken).NoSync();
                await _fileUtil.TryDelete(Path.Combine(fullDatabaseDirectory, "freshclam.dat"), log: false, cancellationToken).NoSync();
                _logger.LogInformation("Updated ClamAV definitions in {DatabaseDirectory}; FreshClam returned {OutputLineCount} output lines",
                    fullDatabaseDirectory, output.Count);
                return output;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("FreshClam update was cancelled for {DatabaseDirectory}", fullDatabaseDirectory);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "FreshClam failed to update {DatabaseDirectory}", fullDatabaseDirectory);
            throw;
        }
        finally
        {
            await _fileUtil.TryDelete(configurationPath, log: false, CancellationToken.None).NoSync();
        }
    }

    public async ValueTask<bool> HasDefinitions(string databaseDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        string fullDatabaseDirectory = Path.GetFullPath(databaseDirectory);

        if (!await _directoryUtil.Exists(fullDatabaseDirectory, cancellationToken).NoSync())
            return false;

        foreach (string extension in _definitionExtensions)
        {
            if ((await _directoryUtil.GetFilesByExtension(fullDatabaseDirectory, extension, recursive: false, cancellationToken).NoSync()).Count > 0)
            {
                _logger.LogDebug("Found ClamAV {DefinitionExtension} definitions in {DatabaseDirectory}", extension, fullDatabaseDirectory);
                return true;
            }
        }

        _logger.LogDebug("No supported ClamAV definitions were found in {DatabaseDirectory}", fullDatabaseDirectory);
        return false;
    }

    public async ValueTask<string> GetVersion(CancellationToken cancellationToken = default)
    {
        (string runtimeDirectory, string freshclamPath) = await GetRuntimePaths(cancellationToken).NoSync();
        await EnsureToolExists(freshclamPath, cancellationToken).NoSync();
        EnsureExecutable(freshclamPath);
        string configurationPath = await _pathUtil.GetRandomTempFilePath(".conf", cancellationToken).NoSync();

        try
        {
            await _fileUtil.Write(configurationPath, BuildConfiguration(new FreshclamOptions()), log: false, cancellationToken).NoSync();
            string arguments = $"--config-file={Quote(configurationPath)} --version";
            List<string> output = await _processUtil.Start(freshclamPath, runtimeDirectory, arguments, log: false,
                environmentalVars: BuildEnvironment(runtimeDirectory), cancellationToken: cancellationToken).NoSync();
            return output.Count == 0 ? string.Empty : output[0];
        }
        finally
        {
            await _fileUtil.TryDelete(configurationPath, log: false, CancellationToken.None).NoSync();
        }
    }

    private async ValueTask ValidateDatabases(string databaseDirectory, CancellationToken cancellationToken)
    {
        foreach (string database in _requiredDatabases)
        {
            bool exists = await _fileUtil.Exists(Path.Combine(databaseDirectory, $"{database}.cvd"), cancellationToken).NoSync() ||
                          await _fileUtil.Exists(Path.Combine(databaseDirectory, $"{database}.cld"), cancellationToken).NoSync();
            if (!exists)
                throw new InvalidDataException($"FreshClam did not produce the required '{database}' database in '{databaseDirectory}'.");
        }
    }

    private Dictionary<string, string>? BuildEnvironment(string runtimeDirectory)
    {
        if (_windows)
            return null;

        string libraryPath = string.Join(Path.PathSeparator, Path.Combine(runtimeDirectory, "lib64"), Path.Combine(runtimeDirectory, "lib"));
        string? existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(existing))
            libraryPath = $"{libraryPath}{Path.PathSeparator}{existing}";

        return new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = libraryPath,
            ["CVD_CERTS_DIR"] = Path.Combine(runtimeDirectory, "etc", "certs")
        };
    }

    private static string BuildConfiguration(FreshclamOptions options)
    {
        using var builder = new PooledStringBuilder(192);
        builder.Append("DatabaseMirror ");
        builder.AppendLine(options.DatabaseMirror);
        builder.AppendLine("ScriptedUpdates yes");
        builder.AppendLine("CompressLocalDatabase no");
        builder.Append("Checks ");
        builder.AppendLine(options.ChecksPerDay.ToString());

        if (RuntimeUtil.IsLinux())
        {
            builder.Append("DatabaseOwner ");
            builder.AppendLine(Environment.UserName);
        }

        return builder.ToString();
    }

    private async ValueTask<(string RuntimeDirectory, string FreshclamPath)> GetRuntimePaths(CancellationToken cancellationToken)
    {
        string runtimeDirectory = await _resourcesPathUtil.GetResourceFilePath(Path.Combine(_runtimeIdentifier, "freshclam"), cancellationToken).NoSync();
        string binaryDirectory = _windows ? runtimeDirectory : Path.Combine(runtimeDirectory, "bin");
        return (runtimeDirectory, Path.Combine(binaryDirectory, _windows ? "freshclam.exe" : "freshclam"));
    }

    private async ValueTask EnsureToolExists(string freshclamPath, CancellationToken cancellationToken)
    {
        if (!await _fileUtil.Exists(freshclamPath, cancellationToken).NoSync())
            throw new FileNotFoundException("The bundled FreshClam executable was not found.", freshclamPath);
    }

    private static void Validate(FreshclamOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseMirror);
        if (options.ChecksPerDay is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(options), "ChecksPerDay must be between 1 and 50.");
        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be greater than zero.");
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void EnsureSupportedPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || (!RuntimeUtil.IsLinux() && !RuntimeUtil.IsWindows()))
            throw new PlatformNotSupportedException("Soenneker.Clamav.Freshclam.Util supports Linux x64 and Windows x64.");
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
