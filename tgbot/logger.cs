using System.Runtime.CompilerServices;

namespace TikTok_bot
{
    /// <summary>
    /// Logging levels for message classification.
    /// </summary>
    public enum LogLevel
    {
        DEBUG,
        INFO,
        WARNING,
        ERROR,
        CRITICAL
    }

    /// <summary>
    /// A static class for logging messages to the console with automatic module detection.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static LogLevel _minLevel = LogLevel.INFO;

        /// <summary>
        /// Configures the minimum logging level.
        /// </summary>
        /// <param name="minLevel">The minimum level to log.</param>
        public static void Configure(LogLevel minLevel)
        {
            _minLevel = minLevel;
        }

        /// <summary>
        /// Writes a message to the log if its level is not lower than the minimum configured level.
        /// </summary>
        /// <param name="level">The logging level.</param>
        /// <param name="message">The message text.</param>
        /// <param name="sourceFilePath">The path to the file from which the call was made (provided by the compiler).</param>
        private static void Log(LogLevel level, string message, [CallerFilePath] string sourceFilePath = "")
        {
            if (level < _minLevel) return;

            // Form the module name from the file name and add a prefix
            var module = $"tgbot.{Path.GetFileNameWithoutExtension(sourceFilePath)}";

            // Lock for thread-safe console output
            lock (_lock)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} - {module} - {level} - {message}");
            }
        }

        public static void Debug(string message, [CallerFilePath] string sourceFilePath = "") => Log(LogLevel.DEBUG, message, sourceFilePath);
        public static void Info(string message, [CallerFilePath] string sourceFilePath = "") => Log(LogLevel.INFO, message, sourceFilePath);
        public static void Warning(string message, [CallerFilePath] string sourceFilePath = "") => Log(LogLevel.WARNING, message, sourceFilePath);
        public static void Error(string message, [CallerFilePath] string sourceFilePath = "") => Log(LogLevel.ERROR, message, sourceFilePath);
        public static void Critical(string message, [CallerFilePath] string sourceFilePath = "") => Log(LogLevel.CRITICAL, message, sourceFilePath);
    }
}