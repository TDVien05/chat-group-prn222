namespace ChatClient.Infrastructure.Storage;

public interface IFileTransferStorage
{
    string StorageRoot { get; }
    Task<string> StoreFileAsync(string tempFilePath, string transferId, string fileName, string roomName, CancellationToken cancellationToken = default);
    Task<(Stream Stream, long Size, string FileName)> OpenFileAsync(string transferId, CancellationToken cancellationToken = default);
    bool FileExists(string transferId);
}
