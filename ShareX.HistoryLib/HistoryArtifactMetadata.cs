#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.IO;

namespace ShareX.HistoryLib
{
    public static class HistoryArtifactMetadata
    {
        public const string HdrFormatTag = "HdrFormat";
        public const string HdrOutputModeTag = "HdrOutputMode";
        public const string HdrMediaTypeTag = "HdrMediaType";
        public const string HdrMasteringPeakNitsTag = "HdrMasteringPeakNits";
        public const string CompanionFilePathTag = "CompanionFilePath";

        public static bool TryGetOwnedCompanionFilePath(
            HistoryItem historyItem,
            out string companionFilePath)
        {
            companionFilePath = null;

            if (historyItem?.Tags == null || string.IsNullOrWhiteSpace(historyItem.FilePath) ||
                !historyItem.Tags.TryGetValue(CompanionFilePathTag, out string taggedPath) ||
                string.IsNullOrWhiteSpace(taggedPath))
            {
                return false;
            }

            try
            {
                string mainPath = Path.GetFullPath(historyItem.FilePath);
                string candidatePath = Path.GetFullPath(taggedPath);
                string mainDirectory = Path.GetDirectoryName(mainPath);
                string candidateDirectory = Path.GetDirectoryName(candidatePath);

                if (string.IsNullOrEmpty(mainDirectory) || string.IsNullOrEmpty(candidateDirectory) ||
                    !mainDirectory.Equals(candidateDirectory, StringComparison.OrdinalIgnoreCase) ||
                    mainPath.Equals(candidatePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string requiredPrefix = Path.GetFileNameWithoutExtension(mainPath) + "-SDR";
                string candidateName = Path.GetFileNameWithoutExtension(candidatePath);
                if (!IsGeneratedCompanionName(candidateName, requiredPrefix))
                {
                    return false;
                }

                companionFilePath = candidatePath;
                return true;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        public static IEnumerable<string> GetOwnedFilePaths(HistoryItem historyItem)
        {
            if (!string.IsNullOrWhiteSpace(historyItem?.FilePath))
            {
                yield return historyItem.FilePath;
            }

            if (TryGetOwnedCompanionFilePath(historyItem, out string companionFilePath))
            {
                yield return companionFilePath;
            }
        }

        public static void SetCompanionFilePath(HistoryItem historyItem, string companionFilePath)
        {
            if (historyItem == null)
            {
                return;
            }

            historyItem.Tags ??= new Dictionary<string, string>();

            if (string.IsNullOrWhiteSpace(companionFilePath))
            {
                historyItem.Tags.Remove(CompanionFilePathTag);
            }
            else
            {
                historyItem.Tags[CompanionFilePathTag] = companionFilePath;
            }
        }

        private static bool IsGeneratedCompanionName(string candidateName, string requiredPrefix)
        {
            if (candidateName.Equals(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string collisionPrefix = requiredPrefix + " (";
            if (!candidateName.StartsWith(collisionPrefix, StringComparison.OrdinalIgnoreCase) ||
                !candidateName.EndsWith(')'))
            {
                return false;
            }

            ReadOnlySpan<char> collisionNumber = candidateName.AsSpan(
                collisionPrefix.Length,
                candidateName.Length - collisionPrefix.Length - 1);
            return int.TryParse(collisionNumber, out int value) && value >= 2;
        }
    }
}
