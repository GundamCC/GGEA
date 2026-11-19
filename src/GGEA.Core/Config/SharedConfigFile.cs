using System.IO;
using GGEA.App.Localization;
using System.Security.Cryptography;
using System.Text;

namespace GGEA.App.Config;

/// <summary>同目录窗口共用配置。按路径串行读写，保存以一次替换发布完整新文件。</summary>
internal sealed class SharedConfigFile : IDisposable
{
    private readonly string _path;
    private readonly Mutex _mutex;

    internal SharedConfigFile(string path)
    {
        _path = Path.GetFullPath(path);
        var key = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
        _mutex = new Mutex(false, "GGEA.Config." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        try
        {
            if (!_mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException(L.Text("配置文件正在使用，请重试。"));
        }
        catch (AbandonedMutexException) { /* 前一个进程退出，当前线程已获得所有权。 */ }
        catch { _mutex.Dispose(); throw; }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    internal string Read() => File.ReadAllText(_path);

    internal void Write(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
