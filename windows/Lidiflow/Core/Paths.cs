using System;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace Lidiflow.Core;

/// Everything the app writes lives in %APPDATA%\Lidiflow (the Windows Application Support).
public static class Paths
{
    public static string Support => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lidiflow"));

    public static string Models => Ensure(Path.Combine(Support, "models"));

    /// whisper.cpp binaries downloaded by the app.
    public static string WhisperBinaries => Ensure(Path.Combine(Support, "whisper"));

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// Writes and recovery for the app's data files.
public static class DataFile
{
    /// Writes through a temporary file, so a crash mid-write never leaves a half-written file.
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// Moves an unreadable file aside (to "name.bad") so the next save doesn't overwrite the user's data.
    public static void SetAside(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write($"Could not set aside {Path.GetFileName(path)}: {e.Message}");
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(Paths.Support, "lidiflow.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

public static class Http
{
    /// Shared client; each call sets its own timeout through a CancellationToken.
    public static readonly HttpClient Client = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static CancellationTokenSource Deadline(double seconds) => new(TimeSpan.FromSeconds(seconds));
}

/// An error whose message is meant for the user (shown in the overlay).
public class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message) { }
}
