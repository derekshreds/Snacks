using Snacks.Models;

namespace Snacks.Services
{
    /// <summary> The class responsible for anything related to file handling </summary>
    public class FileService
    {
        private readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "mkv", "mp4", "ts", "wmv", "avi", "m4v", "mpeg", "mov", "3gp", "webm", "flv"
        };

        /// <summary> Returns whether the app is allowed to access all paths (Electron desktop mode) </summary>
        public bool AllowAllPaths() => Environment.GetEnvironmentVariable("SNACKS_ALLOW_ALL_PATHS") == "true";

        /// <summary> Returns a list of all subdirectories within a folder </summary>
        /// <param name="input"> The path of the directory </param>
        /// <returns> All sub-directories in the path </returns>
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
                // Ignore permission errors and continue
            }

            return dirs;
        }

        /// <summary> Returns a list of all video files within a list of directories </summary>
        /// <param name="directories"> The list of directories to search </param>
        /// <returns> A list of video file paths </returns>
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
                    // Ignore permission errors and continue
                }
            }

            return videoFiles;
        }

        /// <summary> Determines if a file is a video type </summary>
        /// <param name="input"> The path of the file to check </param>
        /// <returns> Boolean value of whether the file is a video </returns>
        public bool IsVideoFile(string input)
        {
            string ext = GetExtension(input);
            if (!_videoExtensions.Contains(ext))
                return false;

            // Skip already-encoded [snacks] files
            string fileName = Path.GetFileNameWithoutExtension(input);
            if (fileName.Contains("[snacks]"))
                return false;

            return true;
        }

        /// <summary> Returns the directory of a file </summary>
        /// <param name="input"> The file path to get the directory from </param>
        /// <returns> The path to the directory </returns>
        public string GetDirectory(string input)
        {
            return Path.GetDirectoryName(input) ?? "";
        }

        /// <summary> Returns the extension of a filename </summary>
        /// <param name="input"> The file path to get the extension from </param>
        /// <returns> The extension of the file </returns>
        public string GetExtension(string input)
        {
            return Path.GetExtension(input).TrimStart('.');
        }

        /// <summary> Removes the extension from a filename </summary>
        /// <param name="input"> The file path to remove the extension from </param>
        /// <returns> The file path with the extension removed </returns>
        public string RemoveExtension(string input)
        {
            return Path.ChangeExtension(input, null) ?? input;
        }

        /// <summary> Returns the file/folder name from a full path </summary>
        /// <param name="input"> The path of the file </param>
        /// <returns> The filename without the path </returns>
        public string GetFileName(string input)
        {
            return Path.GetFileName(input);
        }

        private const int MaxRetries = 10;
        private const int RetryDelayMs = 3000;

        /// <summary> Move a file with retries to handle NAS filesystem delays </summary>
        /// <param name="input"> The path of the file to move </param>
        /// <param name="output"> The path to move the file to </param>
        public async Task FileMoveAsync(string input, string output)
        {
            if (input.Equals(output, StringComparison.OrdinalIgnoreCase))
                return;

            // Ensure output directory exists
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

        /// <summary> Delete a file with retries to handle NAS filesystem delays </summary>
        /// <param name="path"> The path of the file to delete </param>
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

        /// <summary> Retry an operation with exponential backoff for NAS filesystem resilience </summary>
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

        /// <summary> Ensures a directory path ends with a separator </summary>
        /// <param name="path"> The directory path </param>
        /// <returns> The directory path with a trailing separator </returns>
        public string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        }

        /// <summary> Gets the working directory for uploads and processing </summary>
        /// <returns> The working directory path </returns>
        public string GetWorkingDirectory()
        {
            var baseDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR") ?? "/app/work";
            
            if (!Directory.Exists(baseDir))
                Directory.CreateDirectory(baseDir);
                
            return EnsureTrailingSlash(baseDir);
        }

        /// <summary> Gets the uploads directory </summary>
        /// <returns> The uploads directory path </returns>
        public string GetUploadsDirectory()
        {
            var uploadsDir = Path.Combine(GetWorkingDirectory(), "uploads");
            
            if (!Directory.Exists(uploadsDir))
                Directory.CreateDirectory(uploadsDir);
                
            return EnsureTrailingSlash(uploadsDir);
        }

    }
}