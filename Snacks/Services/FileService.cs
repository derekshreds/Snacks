using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     File system utilities for directory traversal, video file detection, and resilient file operations.
/// </summary>
public class FileService
    {
        /// <summary>Video file extensions recognized by the application (case-insensitive).</summary>
        private readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "mkv", "mp4", "ts", "wmv", "avi", "m4v", "mpeg", "mov", "3gp", "webm", "flv"
        };

        /// <summary>Returns <c>true</c> when running in Electron desktop mode, allowing access to all drive paths.</summary>
        public bool AllowAllPaths() => Environment.GetEnvironmentVariable("SNACKS_ALLOW_ALL_PATHS") == "true";

        /// <summary>
        /// Recursively enumerates all subdirectories under <paramref name="input"/>,
        /// including the root directory itself (appended last).
        /// </summary>
        /// <param name="input">The root directory path to walk.</param>
        /// <param name="top">Controls whether the root directory itself is appended to the result. Pass <see langword="true"/> from all external call sites; the recursive calls pass <see langword="false"/> to avoid appending intermediate directories.</param>
        /// <returns>All directories found under <paramref name="input"/>, plus the root itself.</returns>
        public List<string> RecursivelyFindDirectories(string input, bool top = true)
        {
            var dirs = new List<string>();
            
            try
            {
                if (!Directory.Exists(input))
                    return dirs;

                dirs.AddRange(Directory.GetDirectories(input));
                int count = dirs.Count;
                
                for (int i = 0; i < count; i++)
                {
                    dirs.AddRange(RecursivelyFindDirectories(dirs[i], false));
                }

                if (top)
                    dirs.Add(input);
            }
            catch
            {
                // Permission-denied and access errors are non-fatal; skip inaccessible paths.
            }

            return dirs;
        }

        /// <summary>Returns all video files found in the specified directories (non-recursive, one level per directory).</summary>
        /// <param name="directories">The list of directory paths to search.</param>
        /// <returns>All video file paths found across the specified directories.</returns>
        public List<string> GetAllVideoFiles(List<string> directories)
        {
            var videoFiles = new List<string>();

            foreach (var directory in directories)
            {
                try
                {
                    if (!Directory.Exists(directory))
                        continue;

                    var files = Directory.GetFiles(directory);
                    videoFiles.AddRange(files.Where(IsVideoFile));
                }
                catch
                {
                    // Permission-denied and access errors are non-fatal; skip inaccessible paths.
                }
            }

            return videoFiles;
        }

        /// <summary>
        /// Returns <c>true</c> if the file has a recognized video extension and is not
        /// an already-encoded <c>[snacks]</c> output file.
        /// </summary>
        /// <param name="input">The file path to check.</param>
        /// <returns><c>true</c> if the file is a video eligible for encoding.</returns>
        public bool IsVideoFile(string input)
        {
            string ext = GetExtension(input);
            if (!_videoExtensions.Contains(ext))
                return false;

            string fileName = Path.GetFileNameWithoutExtension(input);
            if (fileName.Contains("[snacks]"))
                return false;

            return true;
        }

        /// <summary>Returns the parent directory of a file path, or an empty string if none.</summary>
        /// <param name="input">The file path.</param>
        /// <returns>The directory component of the path.</returns>
        public string GetDirectory(string input)
        {
            return Path.GetDirectoryName(input) ?? "";
        }

        /// <summary>Returns the file extension without the leading dot (e.g., "mkv").</summary>
        /// <param name="input">The file path.</param>
        /// <returns>The extension without a leading dot.</returns>
        public string GetExtension(string input)
        {
            return Path.GetExtension(input).TrimStart('.');
        }

        /// <summary>Returns the file path with the extension removed.</summary>
        /// <param name="input">The file path.</param>
        /// <returns>The path without its extension.</returns>
        public string RemoveExtension(string input)
        {
            return Path.ChangeExtension(input, null) ?? input;
        }

        /// <summary>Returns just the file name and extension from a full path.</summary>
        /// <param name="input">The full file path.</param>
        /// <returns>The file name with extension.</returns>
        public string GetFileName(string input)
        {
            return Path.GetFileName(input);
        }

        /// <summary>Maximum number of retry attempts for file operations.</summary>
        private const int MaxRetries = 10;

        /// <summary>Base delay in milliseconds between retry attempts (multiplied by attempt number).</summary>
        private const int RetryDelayMs = 3000;

        /// <summary>
        /// Moves a file with retry logic to handle transient NAS filesystem errors.
        /// Creates the destination directory if it does not exist.
        /// No-op if source and destination are the same path.
        /// </summary>
        /// <param name="input">Source file path.</param>
        /// <param name="output">Destination file path.</param>
        public async Task FileMoveAsync(string input, string output)
        {
            if (input.Equals(output, StringComparison.OrdinalIgnoreCase))
                return;

            var outputDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            await RetryAsync(() =>
            {
                if (File.Exists(output))
                    File.Delete(output);

                File.Move(input, output);
                return Task.CompletedTask;
            }, $"Move {Path.GetFileName(input)} -> {Path.GetFileName(output)}");
        }

        /// <summary>Deletes a file with retry logic to handle transient NAS filesystem errors. No-op if the file does not exist.</summary>
        /// <param name="path">The path of the file to delete.</param>
        public async Task FileDeleteAsync(string path)
        {
            if (!File.Exists(path))
                return;

            await RetryAsync(() =>
            {
                File.Delete(path);
                return Task.CompletedTask;
            }, $"Delete {Path.GetFileName(path)}");
        }

        /// <summary>
        /// Retries a file operation up to <see cref="MaxRetries"/> times with linearly increasing delays.
        /// Catches <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> for resilience
        /// against NAS filesystem delays and file locks.
        /// </summary>
        /// <param name="operation">The file operation to execute.</param>
        /// <param name="description">Human-readable description for logging retry attempts.</param>
        private async Task RetryAsync(Func<Task> operation, string description)
        {
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    await operation();
                    return;
                }
                catch (IOException) when (attempt < MaxRetries)
                {
                    Console.WriteLine($"File operation retry {attempt}/{MaxRetries}: {description}");
                    await Task.Delay(RetryDelayMs * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < MaxRetries)
                {
                    Console.WriteLine($"File operation retry {attempt}/{MaxRetries} (access denied): {description}");
                    await Task.Delay(RetryDelayMs * attempt);
                }
            }
        }

        /// <summary>Ensures a directory path ends with the platform directory separator character.</summary>
        /// <param name="path">The directory path to normalize.</param>
        /// <returns>The path with a trailing separator, or the original string if null or empty.</returns>
        public string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Returns the application working directory, creating it if needed.
        /// Resolves from the <c>SNACKS_WORK_DIR</c> environment variable, defaulting to <c>/app/work</c>.
        /// </summary>
        /// <returns>The working directory path with a trailing separator.</returns>
        public string GetWorkingDirectory()
        {
            var baseDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR") ?? "/app/work";

            if (!Directory.Exists(baseDir))
                Directory.CreateDirectory(baseDir);

            return EnsureTrailingSlash(baseDir);
        }

        /// <summary>Returns the uploads subdirectory within the working directory, creating it if needed.</summary>
        /// <returns>The uploads directory path with a trailing separator.</returns>
        public string GetUploadsDirectory()
        {
            var uploadsDir = Path.Combine(GetWorkingDirectory(), "uploads");
            
            if (!Directory.Exists(uploadsDir))
                Directory.CreateDirectory(uploadsDir);
                
            return EnsureTrailingSlash(uploadsDir);
        }
}