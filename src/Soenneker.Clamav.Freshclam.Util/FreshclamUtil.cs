using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Clamav.Freshclam.Util.Abstract;
using Soenneker.Clamav.Freshclam.Util.Options;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Path.Abstract;
using Soenneker.Utils.PooledStringBuilders;
using Soenneker.Utils.Process.Abstract;
using Soenneker.Utils.Runtime;

namespace Soenneker.Clamav.Freshclam.Util;

public sealed class FreshclamUtil : IFreshclamUtil
{
    private static readonly SemaphoreSlim _updateLock = new(1, 1);
    private static readonly string[] _definitionExtensions = ["cvd", "cld", "cud", "ndb"];
    private static readonly string[] _requiredDatabases = ["main", "daily", "bytecode"];

    private readonly IProcessUtil _processUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IPathUtil _pathUtil;
    private readonly ILogger<FreshclamUtil> _logger;
    private readonly string _runtimeDirectory;
    private readonly string _freshclamPath;
    private readonly string _defaultDatabaseDirectory;
    private readonly Dictionary<string, string>? _environmentVariables;

    public FreshclamUtil(IProcessUtil processUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IPathUtil pathUtil,
        ILogger<FreshclamUtil> logger)
    {
        _processUtil = processUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _pathUtil = pathUtil;
        _logger = logger;

        EnsureSupportedPlatform();

        bool windows = RuntimeUtil.IsWindows();
        string runtimeIdentifier = windows ? "win-x64" : "linux-x64";
        _runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "Resources", runtimeIdentifier, "freshclam");
        string binaryDirectory = windows ? _runtimeDirectory : Path.Combine(_runtimeDirectory, "bin");
        _freshclamPath = Path.Combine(binaryDirectory, windows ? "freshclam.exe" : "freshclam");
        _defaultDatabaseDirectory = Path.Combine(AppContext.BaseDirectory, "Resources", "clamav-database");

        if (!windows)
            _environmentVariables = BuildLinuxEnvironment();

        _logger.LogDebug("Initialized FreshClam for {RuntimeIdentifier} at {RuntimeDirectory}", runtimeIdentifier, _runtimeDirectory);
    }

    public async ValueTask<IReadOnlyList<string>> Update(string? databaseDirectory = null, FreshclamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FreshclamOptions();
        Validate(options);
        await EnsureToolExists(cancellationToken).NoSync();
        EnsureExecutable(_freshclamPath);

        string fullDatabaseDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(databaseDirectory) ? _defaultDatabaseDirectory : databaseDirectory);
        await _directoryUtil.Create(fullDatabaseDirectory, log: false, cancellationToken).NoSync();
        string configurationPath = await _pathUtil.GetRandomTempFilePath(".conf", cancellationToken).NoSync();

        _logger.LogInformation("Updating ClamAV definitions in {DatabaseDirectory}", fullDatabaseDirectory);
        _logger.LogDebug("Waiting for the FreshClam update lock");
        await _updateLock.WaitAsync(cancellationToken).NoSync();

        try
        {
            _logger.LogDebug("Acquired the FreshClam update lock");
            await _fileUtil.Write(configurationPath, BuildConfiguration(options), log: false, cancellationToken).NoSync();

            string arguments = $"--config-file={Quote(configurationPath)} --datadir={Quote(fullDatabaseDirectory)} --stdout";
            List<string> output = await _processUtil.Start(_freshclamPath, _runtimeDirectory, arguments, timeout: options.Timeout, log: false,
                environmentalVars: _environmentVariables, cancellationToken: cancellationToken).NoSync();

            await ValidateDatabases(fullDatabaseDirectory, cancellationToken).NoSync();
            await _fileUtil.TryDelete(Path.Combine(fullDatabaseDirectory, "freshclam.dat"), log: false, cancellationToken).NoSync();
            _logger.LogInformation("Updated ClamAV definitions in {DatabaseDirectory}; FreshClam returned {OutputLineCount} output lines",
                fullDatabaseDirectory, output.Count);
            return output;
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
            _updateLock.Release();
            _logger.LogDebug("Released the FreshClam update lock");
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
        await EnsureToolExists(cancellationToken).NoSync();
        EnsureExecutable(_freshclamPath);
        string configurationPath = await _pathUtil.GetRandomTempFilePath(".conf", cancellationToken).NoSync();

        try
        {
            await _fileUtil.Write(configurationPath, BuildConfiguration(new FreshclamOptions()), log: false, cancellationToken).NoSync();
            string arguments = $"--config-file={Quote(configurationPath)} --version";
            List<string> output = await _processUtil.Start(_freshclamPath, _runtimeDirectory, arguments, log: false,
                environmentalVars: _environmentVariables, cancellationToken: cancellationToken).NoSync();
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

    private Dictionary<string, string> BuildLinuxEnvironment()
    {
        string libraryPath = string.Join(Path.PathSeparator, Path.Combine(_runtimeDirectory, "lib64"), Path.Combine(_runtimeDirectory, "lib"));
        string? existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(existing))
            libraryPath = $"{libraryPath}{Path.PathSeparator}{existing}";

        return new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = libraryPath,
            ["CVD_CERTS_DIR"] = Path.Combine(_runtimeDirectory, "etc", "certs")
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

    private async ValueTask EnsureToolExists(CancellationToken cancellationToken)
    {
        if (!await _fileUtil.Exists(_freshclamPath, cancellationToken).NoSync())
            throw new FileNotFoundException("The bundled FreshClam executable was not found.", _freshclamPath);
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
