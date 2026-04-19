
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class RepositoryDiscoveryTools
{
    private static readonly string[] IgnoredFolders =
    {
        "bin","obj",".git",".vs","node_modules","dist","build","out","coverage"
    };

    private static readonly string[] IgnoredFileExtensions =
    {
        ".dll",".exe",".pdb",".cache",".log",".tmp"
    };

    private bool ShouldIgnoreDirectory(string path)
    {
        var folder = Path.GetFileName(path);
        return IgnoredFolders.Any(x => folder.Equals(x, StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldIgnoreFile(string path)
    {
        var ext = Path.GetExtension(path);
        return IgnoredFileExtensions.Any(x => ext.Equals(x, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<string> ListRepoTree(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            foreach (var dir in Directory.GetDirectories(current))
            {
                if (ShouldIgnoreDirectory(dir)) continue;
                stack.Push(dir);
            }

            foreach (var file in Directory.GetFiles(current))
            {
                if (ShouldIgnoreFile(file)) continue;
                yield return Path.GetRelativePath(root, file);
            }
        }
    }

    public IEnumerable<string> SearchRepo(string root, string query)
    {
        foreach (var file in ListRepoTree(root))
        {
            var fullPath = Path.Combine(root, file);
            var content = File.ReadAllText(fullPath);

            if (content.Contains(query, StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }
}
