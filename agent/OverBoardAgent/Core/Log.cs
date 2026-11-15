using System.Text;

namespace OverBoardAgent.Core;

/// <summary>Einfaches Protokoll in %LOCALAPPDATA%\OverBoardAgent\agent.log (wird bei 512 KB neu begonnen).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverBoardAgent");
    private static readonly string FilePath = Path.Combine(Dir, "agent.log");

    public static string LogFile => FilePath;

    public static void Info(string text) => Write("INFO", text);
    public static void Warn(string text) => Write("WARN", text);
    public static void Error(string text) => Write("FEHLER", text);

    private static void Write(string level, string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-6} {text}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
                    File.Move(FilePath, FilePath + ".alt", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // Notlösung: Wenn das eigentliche Protokoll nicht schreibbar ist, wenigstens den Grund festhalten
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "OverBoardAgent-Notprotokoll.txt"),
                    $"{DateTime.Now:s} Protokoll {FilePath} nicht schreibbar: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{line}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // nichts mehr zu retten
            }
        }
    }
}
