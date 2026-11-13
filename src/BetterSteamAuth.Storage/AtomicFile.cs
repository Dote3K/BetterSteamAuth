using System.Text;

namespace BetterSteamAuth.Storage;

/// <summary>Crash-safe file replacement: write a temp file, flush it, then swap it in.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents, string? backupPath = null)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            if (backupPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            }

            File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
