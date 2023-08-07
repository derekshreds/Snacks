using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Snacks
{
    public static class FileHandling
    {
        /// <summary>
        /// Returns a list of all subdirectories within a folder
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static List<string> RecursivelyFindDirectories(string input)
        {
            List<string> dirs = Directory.GetDirectories(input).ToList();
            // Neccessary to keep count accurate at the top level
            int count = dirs.Count;
            
            for (int i = 0; i < count; i++)
            {
                dirs.AddRange(RecursivelyFindDirectories(dirs[i]));
            }

            return dirs;
        }

        /// <summary>
        /// Returns a list of all files within a list of directories
        /// </summary>
        /// <param name="directories"></param>
        /// <returns></returns>
        public static List<string> GetAllVideoFiles(List<string> directories)
        {
            var files = new List<string>();
            var video_files = new List<string>();

            for (int i = 0; i < directories.Count; i++)
            {
                files.AddRange(Directory.GetFiles(directories[i]));
            }

            for (int i = 0; i < files.Count; i++)
            {
                if (IsVideoFile(files[i]))
                    video_files.Add(files[i]);
            }

            return video_files;
        }

        /// <summary>
        /// Determines if a file is a video
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static bool IsVideoFile(string input)
        {
            string extension = GetExtension(input);

            if (extension == "mkv" || extension == "mp4" || extension == "ts" || extension == "wmv" ||
                extension == "avi" || extension == "m4v" || extension == "mpeg" || extension == "mov" ||
                extension == "3gp")
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the current directory, or up one level if
        /// parent is specified
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string GetDirectory(string input, bool parent = false)
        {
            string _input = "" + input;
            _input = _input.Replace('\\', '/');
            FileAttributes attributes = File.GetAttributes(_input);

            if ((attributes & FileAttributes.Directory) != FileAttributes.Directory)
            {
                _input = _input.Substring(0, _input.LastIndexOf('/'));
            }

            if (parent)
            {
                _input = _input.Substring(0, _input.LastIndexOf('/'));
            }

            return _input;
        }

        /// <summary>
        /// Returns the extension of a filename
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string GetExtension(string input)
        {
            string[] split = input.Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            string extension = split[split.Length - 1];

            return extension;
        }

        /// <summary>
        /// Returns the file/folder name from a full path
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string GetFileName(string input)
        {
            input = input.Replace('\\', '/');
            string[] split = input.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            return split[split.Length - 1];
        }
    }
}
