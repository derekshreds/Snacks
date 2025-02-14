﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Snacks
{
    /// <summary> The class responsible for anything related to file handling </summary>
    public static class FileHandling
    {
        /// <summary> Returns a list of all subdirectories within a folder </summary>
        /// <param name="input"> The path of the directory </param>
        /// <returns> All sub-directories in the path </returns>
        public static List<string> RecursivelyFindDirectories(string input, bool top = true)
        {
            List<string> dirs = Directory.GetDirectories(input).ToList();
            // Necessary to keep count accurate at the top level
            int count = dirs.Count;
            
            for (int i = 0; i < count; i++)
                dirs.AddRange(RecursivelyFindDirectories(dirs[i].Replace('\\', '/'), false));

            if (top)
                dirs.Add(input);

            return dirs;
        }

        /// <summary> Returns a list of all video files within a list of directories </summary>
        /// <param name="directories"> The list of directories to search </param>
        /// <returns> A list of video file paths </returns>
        public static List<string> GetAllVideoFiles(List<string> directories)
        {
            var videoFiles = new List<string>();

            for (int i = 0; i < directories.Count; i++)
            {
                var files = Directory.GetFiles(directories[i]);

                for (int j = 0; j < files.Count(); j++)
                {
                    if (files[j].IsVideoFile())
                        videoFiles.Add(files[j].Replace('\\', '/'));
                }
            }

            return videoFiles;
        }

        /// <summary> Determines if a file is a video type </summary>
        /// <param name="input"> The path of the file to check </param>
        /// <returns> Boolean value of whether the file is a video </returns>
        public static bool IsVideoFile(this string input)
        {
            string ext = input.GetExtension();

            if (ext == "mkv" || ext == "mp4" || ext == "ts" || ext == "wmv" || ext == "avi" ||
                ext == "m4v" || ext == "mpeg" || ext == "mov" || ext == "3gp")
                return true;
            else
                return false;
        }

        /// <summary> Returns the directory of a file </summary>
        /// <param name="input"> The file path to get the directory from </param>
        /// <returns> The path to the directory </returns>
        public static string GetDirectory(this string input)
        {
            string dir = input.Replace('\\', '/');
            dir = dir.Substring(0, dir.LastIndexOf('/') + 1);

            return dir;
        }

        /// <summary> Returns the extension of a filename </summary>
        /// <param name="input"> The file path to get the extension from </param>
        /// <returns> The extension of the file </returns>
        public static string GetExtension(this string input)
        {
            string[] split = input.Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            string extension = split[split.Length - 1];

            return extension;
        }

        /// <summary> Removes the extension from a filename </summary>
        /// <param name="input"> The file path to remove the extension from </param>
        /// <returns> The file path with the extension removed </returns>
        public static string RemoveExtension(this string input)
        {
            return input.Substring(0, input.LastIndexOf("."));
        }

        /// <summary> Returns the file/folder name from a full path </summary>
        /// <param name="input"> The path of the file </param>
        /// <returns> The filename without the path </returns>
        public static string GetFileName(this string input)
        {
            input = input.Replace('\\', '/');
            string[] split = input.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            return split[split.Length - 1];
        }

        /// <summary> Move a file after deleting anything in the way </summary>
        /// <param name="input"> The path of the file to move </param>
        /// <param name="output"> The path to move the file to </param>
        public static void FileMove(string input, string output)
        {
            try
            {
                if (input.Equals(output))
                    return;

                if (File.Exists(output))
                    File.Delete(output);

                File.Move(input, output);
            }
            catch { }
        }
    }
}
