using System.Collections.Concurrent;
using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Maps a directory to the git repository (or worktree) containing it, by walking up to the nearest
/// <c>.git</c>. Repository roots are cached per directory; the branch is re-read from <c>HEAD</c>, which
/// is a single small file, so a checkout shows up on the next scan.
/// </summary>
public static class ProjectResolver
{
    private static readonly ConcurrentDictionary<string, string?> Roots = new(StringComparer.Ordinal);
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');

    public static ProjectInfo? Resolve(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Path.IsPathRooted(directory))
        {
            return null;
        }

        var root = Roots.GetOrAdd(directory, FindRoot);
        if (root is null)
        {
            return null;
        }

        var git = Path.Combine(root, ".git");
        var gitDir = git;
        var isWorktree = false;
        string name = Path.GetFileName(root);

        try
        {
            if (File.Exists(git))
            {
                // A linked worktree: ".git" is a file pointing at <main>/.git/worktrees/<name>.
                var pointer = File.ReadAllText(git).Trim();
                if (pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    gitDir = Path.GetFullPath(pointer[7..].Trim(), root);
                    isWorktree = gitDir.Contains($"{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
                    if (isWorktree && Directory.GetParent(gitDir)?.Parent?.Parent is { } mainRoot)
                    {
                        // Name the project after the main checkout, so all its worktrees group together.
                        name = mainRoot.Name;
                    }
                }
            }

            return new ProjectInfo(root, name, ReadBranch(gitDir), isWorktree);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProjectInfo(root, name, null, isWorktree);
        }
    }

    private static string? FindRoot(string directory)
    {
        try
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            {
                var full = dir.FullName.TrimEnd('/', '\\');

                // A dotfiles repo in $HOME is not a project, and neither is the filesystem root.
                if (full == Home || full.Length <= 1 || (full.Length == 2 && full[1] == ':'))
                {
                    return null;
                }

                var git = Path.Combine(full, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    return full;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return null;
    }

    private static string? ReadBranch(string gitDir)
    {
        var headPath = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        var head = File.ReadAllText(headPath).Trim();
        const string prefix = "ref: refs/heads/";
        if (head.StartsWith(prefix, StringComparison.Ordinal))
        {
            return head[prefix.Length..];
        }

        // Detached HEAD: show the short commit.
        return head.Length >= 7 ? head[..7] : null;
    }
}
