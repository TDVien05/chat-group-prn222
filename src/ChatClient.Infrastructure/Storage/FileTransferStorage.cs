using System.IO;

namespace ChatClient.Infrastructure.Storage;

public sealed class FileTransferStorage : IFileTransferStorage
{
    private readonly string _storageRoot;

    public FileTransferStorage(string storageRoot)
    {
        _storageRoot = storageRoot;
        Directory.CreateDirectory(storageRoot);
    }

    public string StorageRoot => _storageRoot;

    public Task<string> StoreFileAsync(string tempFilePath, string transferId, string fileName, string roomName, CancellationToken cancellationToken = default)
    {
        var roomDir = Path.Combine(_storageRoot, SanitizeName(roomName));
        Directory.CreateDirectory(roomDir);

        var extension = Path.GetExtension(fileName);
        var safeName = SanitizeName(Path.GetFileNameWithoutExtension(fileName));
        var storedFileName = $"{transferId}_{safeName}{extension}";
        var destPath = Path.Combine(roomDir, storedFileName);

        File.Move(tempFilePath, destPath, overwrite: true);
        return Task.FromResult(storedFileName);
    }

    public Task<(Stream Stream, long Size, string FileName)> OpenFileAsync(string transferId, CancellationToken cancellationToken = default)
    {
        foreach (var dir in Directory.GetDirectories(_storageRoot))
        {
            var files = Directory.GetFiles(dir, $"{transferId}_*");
            if (files.Length > 0)
            {
                var path = files[0];
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                var size = new FileInfo(path).Length;
                var name = Path.GetFileName(path)[(transferId.Length + 1)..]; // strip "transferId_" prefix
                return Task.FromResult<(Stream, long, string)>((stream, size, name));
            }
        }
        throw new FileNotFoundException($"File with transfer ID '{transferId}' not found.");
    }

    public bool FileExists(string transferId)
    {
        foreach (var dir in Directory.GetDirectories(_storageRoot))
        {
            if (Directory.GetFiles(dir, $"{transferId}_*").Length > 0)
                return true;
        }
        return false;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
