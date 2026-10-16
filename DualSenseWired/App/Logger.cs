// Log to %LOCALAPPDATA%\DualSenseWired\DualSenseWired.log (and the console when there is one).

namespace DualSenseWired.App;

public static class Logger
{
    static readonly object Lock = new();
    static StreamWriter? _file;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseWired", "DualSenseWired.log");

    public static void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Lock)
        {
            try
            {
                if (_file == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    _file = new StreamWriter(FilePath, append: false) { AutoFlush = true };
                }
                _file.WriteLine(line);
            }
            catch (IOException)
            {
            }
            Console.WriteLine(line);
        }
    }
}
