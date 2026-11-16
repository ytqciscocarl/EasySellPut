using System.Security.Cryptography;
using System.Text;

namespace Meili.Core.Settings;

/// <summary>A lost-update error: callers must reload before retrying their edit.</summary>
public sealed class DataConflictException(string path) : IOException($"Local data changed in another process: {path}. Reload before saving.");

/// <summary>Atomic JSON replacement with a cross-process lock and optimistic snapshot check.</summary>
public sealed class SnapshotFile
{
    private readonly string _path;
    private string? _expected;
    public SnapshotFile(string path)
    {
        _path = path;
        _expected = Fingerprint();
    }

    public string? Read()
    {
        if (!File.Exists(_path)) { _expected = null; return null; }
        var bytes = File.ReadAllBytes(_path);
        _expected = Convert.ToHexString(SHA256.HashData(bytes));
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    public void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var lease = Acquire();
        if (Fingerprint() != _expected) throw new DataConflictException(_path);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
            _expected = Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string? Fingerprint() => File.Exists(_path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_path))) : null;

    private FileStream Acquire()
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); } // expected: another local writer owns the lease
        }
    }
}
