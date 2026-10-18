using System;
using System.Collections.Generic;
using System.IO;

namespace AgentDock;

/// <summary>Reads the current git branch for a folder straight from .git/HEAD (no git process).</summary>
public static class GitInfo
{
    static readonly Dictionary<string, (string branch, DateTime read)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Branch(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "";
        if (Cache.TryGetValue(cwd, out var hit) && DateTime.Now - hit.read < TimeSpan.FromSeconds(15)) return hit.branch;

        string branch = "";
        try
        {
            for (var dir = new DirectoryInfo(cwd); dir != null; dir = dir.Parent)
            {
                string git = Path.Combine(dir.FullName, ".git");
                string? gitDir = null;
                if (Directory.Exists(git)) gitDir = git;
                else if (File.Exists(git))
                {
                    // Worktrees and submodules: ".git" is a file with "gitdir: <path>".
                    string line = File.ReadAllText(git).Trim();
                    if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        string target = line[7..].Trim();
                        gitDir = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(dir.FullName, target));
                    }
                }
                if (gitDir == null) continue;

                string head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                branch = head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head[16..]
                       : head.Length >= 7 ? head[..7] : head;
                break;
            }
        }
        catch (Exception)
        {
            // Not a repo, or unreadable: show no branch.
        }
        Cache[cwd] = (branch, DateTime.Now);
        return branch;
    }
}
