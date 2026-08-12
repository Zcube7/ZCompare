namespace ZCompare.Tests.Fixtures;

public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ZCompare.Tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Directory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not hide the comparison assertion that just ran.
        }
        catch (UnauthorizedAccessException)
        {
            // Windows virus scanners can briefly retain generated XLSX files.
        }
    }
}
