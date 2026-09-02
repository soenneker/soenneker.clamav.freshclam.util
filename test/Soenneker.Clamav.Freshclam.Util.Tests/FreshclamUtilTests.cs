using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Clamav.Freshclam.Util.Abstract;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Tests.HostedUnit;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Clamav.Freshclam.Util.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FreshclamUtilTests : HostedUnitTest
{
    private readonly IFreshclamUtil _util;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IFileUtil _fileUtil;

    public FreshclamUtilTests(Host host) : base(host)
    {
        _util = Resolve<IFreshclamUtil>(true);
        _directoryUtil = Resolve<IDirectoryUtil>(true);
        _fileUtil = Resolve<IFileUtil>(true);
    }

    [Test]
    public async Task Gets_bundled_version(CancellationToken cancellationToken)
    {
        string version = await _util.GetVersion(cancellationToken).NoSync();
        await Assert.That(version).StartsWith("ClamAV ");
    }

    [Test]
    public async Task HasDefinitions_detects_a_database_file(CancellationToken cancellationToken)
    {
        string directory = await _directoryUtil.CreateTempDirectory(cancellationToken).NoSync();

        try
        {
            await _fileUtil.Write(Path.Combine(directory, "local.ndb"), "test", log: false, cancellationToken).NoSync();
            bool result = await _util.HasDefinitions(directory, cancellationToken).NoSync();
            await Assert.That(result).IsTrue();
        }
        finally
        {
            await _directoryUtil.DeleteIfExists(directory, cancellationToken).NoSync();
        }
    }
}
