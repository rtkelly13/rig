using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>
/// Deletes a folder and everything under it. Ported from <c>src/rig/estate/trim.py</c> <c>remove_tree</c>.
/// </summary>
/// <remarks>
/// <para>
/// A link inside the tree, a symlink or a junction, is removed as a name and never entered: pnpm links whole packages in
/// from a store every clone shares. A read-only file or folder is made writable and deleted, and on Unix a folder its
/// owner cannot list or write is opened up to that owner first.
/// </para>
/// <para>
/// Windows adds two ACL cases. A uv <c>.venv</c> hardlinks files from uv's cache, and some carry an ACL denying the
/// user delete; changing it would change every link, so delete-child is granted on the parent instead. And Python
/// 3.13.0 wrote a protected ACL on folders it made with mode 0o700 (pytest's <c>.pytest_cache</c>), locking out its
/// own owner, who can still reset it.
/// </para>
/// </remarks>
/// <param name="proc">Runs <c>icacls</c> and <c>whoami</c> on Windows.</param>
public sealed class TreeRemoval(IProc proc)
{
    private string? _sid;

    /// <summary>Deletes <paramref name="path"/>. Throws <see cref="IOException"/> when it is a link or cannot be removed.</summary>
    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(path);
        if (root.LinkTarget is null && !root.Exists)
        {
            throw new DirectoryNotFoundException($"{path} does not exist.");
        }

        if (root.LinkTarget is not null)
        {
            throw new IOException($"{path} is a link; removing a tree never follows one.");
        }

        await RemoveFolderAsync(root, null, cancellationToken).ConfigureAwait(false);
    }

    // <paramref name="parent"/> is null for the root: what holds the tree is never opened up to remove it.
    private async Task RemoveFolderAsync(DirectoryInfo folder, DirectoryInfo? parent, CancellationToken cancellationToken)
    {
        foreach (var entry in await ListAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo child && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                await RemoveFolderAsync(child, folder, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeleteAsync(entry, folder, cancellationToken).ConfigureAwait(false);
            }
        }

        await DeleteAsync(folder, parent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<FileSystemInfo>> ListAsync(DirectoryInfo folder, CancellationToken cancellationToken)
    {
        try
        {
            return List(folder);
        }
        catch (UnauthorizedAccessException)
        {
            await UnlockAsync(folder, cancellationToken).ConfigureAwait(false);
            return List(folder);
        }
    }

    private static List<FileSystemInfo> List(DirectoryInfo folder) =>
        [.. folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })];

    // A link (to a folder or a file) and a plain file go by name; an emptied folder by rmdir.
    private async Task DeleteAsync(FileSystemInfo entry, DirectoryInfo? parent, CancellationToken cancellationToken)
    {
        try
        {
            Delete(entry);
        }
        catch (UnauthorizedAccessException)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
                if (TryDelete(entry))
                {
                    return;
                }
            }

            // Still denied: the folder holding it is what refuses, so open that, never the tree's own parent.
            if (parent is not null)
            {
                await AllowDeleteAsync(parent, cancellationToken).ConfigureAwait(false);
            }

            Delete(entry);
        }
    }

    private static bool TryDelete(FileSystemInfo entry)
    {
        try
        {
            Delete(entry);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Delete(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo)
        {
            Directory.Delete(entry.FullName, recursive: false);
        }
        else
        {
            File.Delete(entry.FullName);
        }
    }

    // A folder whose owner may not list it: give the owner rwx on Unix, reset the ACL on Windows.
    private async Task UnlockAsync(DirectoryInfo folder, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await IcaclsAsync([folder.FullName, "/reset", "/q"], cancellationToken).ConfigureAwait(false);
            return;
        }

        OpenToOwner(folder.FullName);
    }

    // A file that may not be unlinked: its folder needs write on Unix, delete-child for this user on Windows.
    private async Task AllowDeleteAsync(DirectoryInfo parent, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await GrantDeleteChildAsync(parent.FullName, cancellationToken).ConfigureAwait(false);
            return;
        }

        OpenToOwner(parent.FullName);
    }

    [UnsupportedOSPlatform("windows")]
    private static void OpenToOwner(string folder) =>
        File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    [ExcludeFromCodeCoverage(Justification = "Windows-only ACL repair; no test creates a deny-delete or protected ACL.")]
    private async Task GrantDeleteChildAsync(string folder, CancellationToken cancellationToken)
    {
        if (_sid is null)
        {
            var who = await proc.RunAsync(new ProcRequest(["whoami", "/user", "/fo", "csv", "/nh"]), cancellationToken).ConfigureAwait(false);
            _sid = who.Ok ? who.Out.Trim().Split(',')[^1].Trim('"') : throw new IOException($"whoami: {who.Err.Trim()}");
        }

        await IcaclsAsync([folder, "/grant", $"*{_sid}:(DC)"], cancellationToken).ConfigureAwait(false);
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only ACL repair; no test creates a deny-delete or protected ACL.")]
    private async Task IcaclsAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        _ = await proc.RunAsync(new ProcRequest(["icacls", .. args]), cancellationToken).ConfigureAwait(false);
}
