using Rig.Core.Estate;

namespace Rig.Core.Tests.Estate;

/// <summary>Sizes a folder by the lengths of the files under it, so a test's numbers do not depend on block sizes.</summary>
internal sealed class Walked : IDiskUsage
{
    public Task<long> SizeAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(path) ? new FileInfo(path).Length
            : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
            : 0);
}
