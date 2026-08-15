using System.Text;
using Microsoft.UI.Xaml;

namespace TamizChat.Services;

/// <summary>
/// Writes down why the app died.
///
/// A WinUI app that hits an unhandled exception simply vanishes: no dialog, no
/// message, and nothing in the window that was there a moment ago. That happened
/// during a long upload and left nothing at all to go on — which is the reason
/// this exists. It is not error handling; it is the note the app leaves behind
/// so the next crash can be read instead of guessed at.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>Where the note is left: next to the executable, as crash.log.</summary>
    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");

    /// <summary>
    /// Hooks every way an exception can escape.
    ///
    /// All three are needed and they catch different things: the XAML handler
    /// sees what happens on the interface thread, the AppDomain one sees threads
    /// nobody is awaiting, and the task one sees exceptions from work that was
    /// started and then forgotten.
    /// </summary>
    public static void Install(Application app)
    {
        app.UnhandledException += (_, e) =>
        {
            Write("UI thread", e.Exception);

            // Not handled: swallowing it would leave the app in a state nobody
            // designed. The point is only that it is written down first.
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("background thread", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Records something that went wrong without ending the app.</summary>
    public static void Write(string where, Exception? error)
    {
        if (error is null)
        {
            return;
        }

        var note = new StringBuilder()
            .AppendLine($"--- {DateTimeOffset.Now:u}  ({where}) ---")
            .AppendLine(error.ToString())
            .AppendLine()
            .ToString();

        try
        {
            // Locked and appended: two threads can fail at once, and the second
            // one's report is worth as much as the first.
            lock (Gate)
            {
                File.AppendAllText(Path, note);
            }
        }
        catch (Exception)
        {
            // A crash log that cannot be written must not itself crash anything.
        }
    }
}
