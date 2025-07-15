namespace TikTok_bot
{
    /// <summary>
    /// Utility class for file system operations.
    /// </summary>
    public static class FileIO
    {
        // Default timeout for stale files (30 minutes in seconds)
        private const int DEFAULT_STALE_FILE_TIMEOUT_SECONDS = 1800;
        
        // Default cleanup interval (5 minutes in seconds)
        private const int DEFAULT_CLEANUP_INTERVAL_SECONDS = 300;
        
        // Cancellation token source for cleanup task
        private static CancellationTokenSource? _cleanupCts;

        /// <summary>
        /// Converts a file:// URL to a local file path.
        /// </summary>
        /// <param name="fileUrl">The file URL starting with file://</param>
        /// <returns>The local file system path</returns>
        public static string FileUrlToLocalPath(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl))
                return string.Empty;

            if (fileUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return fileUrl.Replace("file://", "").Replace('/', Path.DirectorySeparatorChar);

            return fileUrl;
        }

        /// <summary>
        /// Checks if a file exists at the specified path.
        /// </summary>
        /// <param name="path">The file path to check</param>
        /// <returns>True if the file exists, false otherwise</returns>
        public static bool FileExists(string path)
        {
            return File.Exists(path);
        }

        /// <summary>
        /// Opens a file for reading and returns the stream.
        /// </summary>
        /// <param name="path">The path of the file to open</param>
        /// <returns>FileStream for reading the file</returns>
        public static FileStream OpenFileForReading(string path)
        {
            try
            {
                return File.OpenRead(path);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error opening file: {path} - {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the filename from a full path.
        /// </summary>
        /// <param name="path">The full file path</param>
        /// <returns>Just the filename portion</returns>
        public static string GetFileName(string path)
        {
            return Path.GetFileName(path);
        }

        /// <summary>
        /// Deletes a file and logs the result.
        /// </summary>
        /// <param name="path">Path of the file to delete</param>
        /// <returns>True if deletion was successful, false otherwise</returns>
        public static bool DeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !FileExists(path))
                return false;

            try
            {
                File.Delete(path);
                Logger.Info($"File deleted: {path}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to delete file: {path} - Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Asynchronously deletes a file after a specified delay.
        /// </summary>
        /// <param name="path">Path of the file to delete</param>
        /// <param name="delayMs">Delay in milliseconds before deletion</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public static async Task DeleteFileWithDelayAsync(string path, int delayMs = 1000)
        {
            if (string.IsNullOrEmpty(path) || !FileExists(path))
                return;

            await Task.Delay(delayMs);
            DeleteFile(path);
        }

        /// <summary>
        /// Provides a FileStream and filename for a file, handling both local paths and file:// URLs.
        /// </summary>
        /// <param name="fileUrl">The file URL or path</param>
        /// <param name="stream">Output parameter that will contain the file stream if successful</param>
        /// <param name="fileName">Output parameter that will contain the filename</param>
        /// <returns>True if the file was successfully opened, false otherwise</returns>
        public static bool TryGetFileStreamAndName(string fileUrl, out FileStream? stream, out string fileName)
        {
            stream = null;
            fileName = string.Empty;

            try
            {
                string localPath = FileUrlToLocalPath(fileUrl);
                
                if (!FileExists(localPath))
                    return false;

                stream = OpenFileForReading(localPath);
                fileName = GetFileName(localPath);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Starts a background task to clean up stale files in the specified directory.
        /// </summary>
        /// <param name="directory">Directory to monitor for stale files</param>
        /// <param name="filePattern">File pattern to match (e.g., "*.mp4")</param>
        /// <param name="staleTimeoutSeconds">Time in seconds after which a file is considered stale</param>
        /// <param name="cleanupIntervalSeconds">How often to run the cleanup task (in seconds)</param>
        public static void StartFileCleanupTask(
            string directory, 
            string filePattern = "*.*", 
            int staleTimeoutSeconds = DEFAULT_STALE_FILE_TIMEOUT_SECONDS,
            int cleanupIntervalSeconds = DEFAULT_CLEANUP_INTERVAL_SECONDS)
        {
            // Stop any existing cleanup task
            StopFileCleanupTask();
            
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                Logger.Error($"Invalid directory for file cleanup: {directory}");
                return;
            }
            
            _cleanupCts = new CancellationTokenSource();
            var token = _cleanupCts.Token;
            
            Task.Run(async () => 
            {
                Logger.Info($"File cleanup task started for directory: {directory}");
                Logger.Info($"Files older than {staleTimeoutSeconds} seconds will be deleted");
                Logger.Info($"Cleanup will run every {cleanupIntervalSeconds} seconds");
                
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int deletedCount = CleanupStaleFiles(directory, filePattern, staleTimeoutSeconds);
                        if (deletedCount > 0)
                        {
                            Logger.Info($"Deleted {deletedCount} stale files from {directory}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error in file cleanup task: {ex.Message}");
                    }
                    
                    // Wait for the next cleanup interval
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(cleanupIntervalSeconds), token);
                    }
                    catch (TaskCanceledException)
                    {
                        // Task was canceled, exit the loop
                        break;
                    }
                }
                
                Logger.Info("File cleanup task stopped");
            }, token);
        }
        
        /// <summary>
        /// Stops the background file cleanup task if it's running.
        /// </summary>
        public static void StopFileCleanupTask()
        {
            if (_cleanupCts != null)
            {
                _cleanupCts.Cancel();
                _cleanupCts.Dispose();
                _cleanupCts = null;
            }
        }
        
        /// <summary>
        /// Performs a single cleanup of stale files in the specified directory.
        /// </summary>
        /// <param name="directory">Directory to scan for stale files</param>
        /// <param name="filePattern">File pattern to match</param>
        /// <param name="staleTimeoutSeconds">Seconds after which a file is considered stale</param>
        /// <returns>Number of files deleted</returns>
        public static int CleanupStaleFiles(string directory, string filePattern = "*.*", int staleTimeoutSeconds = DEFAULT_STALE_FILE_TIMEOUT_SECONDS)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return 0;
                
            int deletedCount = 0;
            DateTime cutoffTime = DateTime.Now.AddSeconds(-staleTimeoutSeconds);
            
            try
            {
                string[] files = Directory.GetFiles(directory, filePattern, SearchOption.TopDirectoryOnly);
                
                foreach (string file in files)
                {
                    try
                    {
                        DateTime lastModified = File.GetLastWriteTime(file);
                        
                        if (lastModified < cutoffTime)
                        {
                            if (DeleteFile(file))
                            {
                                deletedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error processing file {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error scanning directory {directory}: {ex.Message}");
            }
            
            return deletedCount;
        }
    }
}