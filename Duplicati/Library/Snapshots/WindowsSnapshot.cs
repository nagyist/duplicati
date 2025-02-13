// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Duplicati.Library.Common.IO;

namespace Duplicati.Library.Snapshots
{
    /// <summary>
    /// This class encapsulates all access to the Windows Volume Shadow copy Services,
    /// implementing the disposable patterns to ensure correct release of resources.
    ///
    /// The class presents all files and folders with their regular filenames to the caller,
    /// and internally handles the conversion to the shadow path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsSnapshot : SnapshotBase
    {
        /// <summary>
        /// The tag used for logging messages
        /// </summary>
        public static readonly string LOGTAG = Logging.Log.LogTagFromType<WindowsSnapshot>();

        /// <summary>
        /// The system IO for the current platform
        /// </summary>
        private static readonly ISystemIO IO_WIN = SystemIO.IO_OS;

        /// <summary>
        /// The main reference to the backup controller
        /// </summary>
        private readonly VssBackupComponents _vssBackupComponents;

        /// <summary>
        /// Constructs a new backup snapshot, using all the required disks
        /// </summary>
        /// <param name="sources">Sources to determine which volumes to include in snapshot</param>
        /// <param name="options">A set of commandline options</param>
        public WindowsSnapshot(IEnumerable<string> sources, IDictionary<string, string> options)
        {
            // For Windows, ensure we don't store paths with extended device path prefixes (i.e., @"\\?\" or @"\\?\UNC\")
            sources = sources.Select(SystemIOWindows.RemoveExtendedDevicePathPrefix);
            try
            {
                _vssBackupComponents = new VssBackupComponents();

                // Default to exclude the System State writer
                var excludedWriters = new Guid[] { new Guid("{e8132975-6f93-4464-a53e-1050253ae220}") };
                if (options.ContainsKey("vss-exclude-writers"))
                {
                    excludedWriters = options["vss-exclude-writers"]
                        .Split(';')
                        .Where(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length > 0)
                        .Select(x => new Guid(x))
                        .ToArray();
                }
                _vssBackupComponents.SetupWriters(null, excludedWriters);

                _vssBackupComponents.InitShadowVolumes(sources);

                _vssBackupComponents.MapVolumesToSnapShots();

                //If we should map the drives, we do that now and update the volumeMap
                if (Utility.Utility.ParseBoolOption(options.AsReadOnly(), "vss-use-mapping"))
                {
                    _vssBackupComponents.MapDrives();
                }
            }
            catch (Exception ex1)
            {

                Logging.Log.WriteVerboseMessage(LOGTAG, "WindowsSnapshotCreation", ex1, "Failed to initialize windows snapshot instance");

                //In case we fail in the constructor, we do not want a snapshot to be active
                try
                {
                    Dispose();
                }
                catch (Exception ex2)
                {
                    Logging.Log.WriteVerboseMessage(LOGTAG, "VSSCleanupOnError", ex2, "Failed during VSS error cleanup");
                }

                throw;
            }
        }

        #region Private functions

        /// <summary>
        /// A callback function that takes a non-shadow path to a folder,
        /// and returns all folders found in a non-shadow path format.
        /// </summary>
        /// <param name="localFolderPath">The non-shadow path of the folder to list</param>
        /// <returns>A list of non-shadow paths</returns>
        protected override string[] ListFolders(string localFolderPath)
        {
            string[] tmp = null;
            var spath = ConvertToSnapshotPath(localFolderPath);
            tmp = IO_WIN.GetDirectories(spath);
            var root = Util.AppendDirSeparator(IO_WIN.GetPathRoot(localFolderPath));
            var volumePath = Util.AppendDirSeparator(ConvertToSnapshotPath(root));
            volumePath = SystemIOWindows.AddExtendedDevicePathPrefix(volumePath);

            for (var i = 0; i < tmp.Length; i++)
            {
                tmp[i] = root + SystemIOWindows.AddExtendedDevicePathPrefix(tmp[i]).Substring(volumePath.Length);
            }

            return tmp;
        }


        /// <summary>
        /// A callback function that takes a non-shadow path to a folder,
        /// and returns all files found in a non-shadow path format.
        /// </summary>
        /// <param name="localFolderPath">The non-shadow path of the folder to list</param>
        /// <returns>A list of non-shadow paths</returns>
        protected override string[] ListFiles(string localFolderPath)
        {

            string[] files = null;
            var spath = ConvertToSnapshotPath(localFolderPath);
            files = IO_WIN.GetFiles(spath);

            // convert back to non-shadow, i.e., non-vss version
            var root = Util.AppendDirSeparator(IO_WIN.GetPathRoot(localFolderPath));
            var volumePath = Util.AppendDirSeparator(ConvertToSnapshotPath(root));
            volumePath = SystemIOWindows.AddExtendedDevicePathPrefix(volumePath);

            for (var i = 0; i < files.Length; i++)
            {
                files[i] = root + SystemIOWindows.AddExtendedDevicePathPrefix(files[i]).Substring(volumePath.Length);
            }

            return files;
        }
        #endregion

        #region ISnapshotService Members

        /// <summary>
        /// Enumerates all files and folders in the snapshot, restricted to sources
        /// </summary>
        /// <param name="sources">Sources to enumerate</param>
        /// <param name="callback">The callback to invoke with each found path</param>
        /// <param name="errorCallback">The callback used to report errors</param>
        public override IEnumerable<string> EnumerateFilesAndFolders(IEnumerable<string> sources, Utility.Utility.EnumerationFilterDelegate callback, Utility.Utility.ReportAccessError errorCallback)
        {
            // For Windows, ensure we don't store paths with extended device path prefixes (i.e., @"\\?\" or @"\\?\UNC\")
            return base.EnumerateFilesAndFolders(sources.Select(SystemIOWindows.RemoveExtendedDevicePathPrefix), callback, errorCallback);
        }

        /// <summary>
        /// Gets the last write time of a given file in UTC
        /// </summary>
        /// <param name="localPath">The full path to the file in non-shadow format</param>
        /// <returns>The last write time of the file</returns>
        public override DateTime GetLastWriteTimeUtc(string localPath)
        {
            var spath = ConvertToSnapshotPath(localPath);

            return IO_WIN.GetLastWriteTimeUtc(SystemIOWindows.AddExtendedDevicePathPrefix(spath));
        }

        /// <summary>
        /// Gets the creation of a given file in UTC
        /// </summary>
        /// <param name="localPath">The full path to the file in non-shadow format</param>
        /// <returns>The last write time of the file</returns>
        public override DateTime GetCreationTimeUtc(string localPath)
        {
            var spath = ConvertToSnapshotPath(localPath);

            return IO_WIN.GetCreationTimeUtc(SystemIOWindows.AddExtendedDevicePathPrefix(spath));
        }

        /// <summary>
        /// Opens a file for reading
        /// </summary>
        /// <param name="localPath">The full path to the file in non-shadow format</param>
        /// <returns>An open filestream that can be read</returns>
        public override Stream OpenRead(string localPath)
        {
            return IO_WIN.FileOpenRead(ConvertToSnapshotPath(localPath));
        }

        /// <summary>
        /// Returns the size of a file
        /// </summary>
        /// <param name="localPath">The full path to the file in non-snapshot format</param>
        /// <returns>The length of the file</returns>
        public override long GetFileSize(string localPath)
        {
            return IO_WIN.FileLength(ConvertToSnapshotPath(localPath));
        }

        /// <summary>
        /// Gets the attributes for the given file or folder
        /// </summary>
        /// <returns>The file attributes</returns>
        /// <param name="localPath">The file or folder to examine</param>
        public override FileAttributes GetAttributes(string localPath)
        {
            return IO_WIN.GetFileAttributes(ConvertToSnapshotPath(localPath));
        }

        /// <summary>
        /// Returns the symlink target if the entry is a symlink, and null otherwise
        /// </summary>
        /// <param name="localPath">The file or folder to examine</param>
        /// <returns>The symlink target</returns>
        public override string GetSymlinkTarget(string localPath)
        {
            var spath = ConvertToSnapshotPath(localPath);
            return IO_WIN.GetSymlinkTarget(spath);
        }

        /// <summary>
        /// Gets the metadata for the given file or folder
        /// </summary>
        /// <returns>The metadata for the given file or folder</returns>
        /// <param name="localPath">The file or folder to examine</param>
        /// <param name="isSymlink">A flag indicating if the target is a symlink</param>
        /// <param name="followSymlink">A flag indicating if a symlink should be followed</param>
        public override Dictionary<string, string> GetMetadata(string localPath, bool isSymlink, bool followSymlink)
        {
            return IO_WIN.GetMetadata(ConvertToSnapshotPath(localPath), isSymlink, followSymlink);
        }

        /// <inheritdoc />
        public override bool IsBlockDevice(string localPath)
        {
            return false;
        }

        /// <inheritdoc />
        public override string HardlinkTargetID(string localPath)
        {
            return null;
        }

        /// <inheritdoc />
        public override string ConvertToLocalPath(string snapshotPath)
        {
            if (!Path.IsPathRooted(snapshotPath))
                throw new InvalidOperationException();

            foreach (var kvp in _vssBackupComponents.SnapshotDeviceAndVolumes)
            {
                if (snapshotPath.StartsWith(kvp.Key, Utility.Utility.ClientFilenameStringComparison))
                    return IO_WIN.PathCombine(kvp.Value, snapshotPath.Substring(kvp.Key.Length));
            }

            throw new InvalidOperationException();
        }

        /// <inheritdoc />
        public override string ConvertToSnapshotPath(string localPath)
        {
            // For Windows, ensure we don't store paths with extended device path prefixes (i.e., @"\\?\" or @"\\?\UNC\")
            localPath = SystemIOWindows.RemoveExtendedDevicePathPrefix(localPath);

            if (!Path.IsPathRooted(localPath))
                throw new InvalidOperationException();

            var root = IO_WIN.GetPathRoot(localPath);
            var volumePath = _vssBackupComponents.GetVolumeFromCache(root);

            // Note that using a simple Path.Combine() for the following code
            // can result in invalid snapshot paths; e.g., if localPath is
            // @"C:\", mappedPath would not have the required trailing
            // directory separator.
            var subPath = localPath.Substring(root.Length);
            if (!subPath.StartsWith(Util.DirectorySeparatorString, StringComparison.Ordinal))
            {
                volumePath = Util.AppendDirSeparator(volumePath, Util.DirectorySeparatorString);
            }

            var mappedPath = volumePath + subPath;
            return mappedPath;
        }

        /// <inheritdoc />
        public override bool FileExists(string localFilePath)
        {
            return IO_WIN.FileExists(ConvertToSnapshotPath(localFilePath));
        }

        /// <inheritdoc />
        public override bool DirectoryExists(string localFolderPath)
        {
            return IO_WIN.DirectoryExists(ConvertToSnapshotPath(localFolderPath));
        }

        /// <inheritdoc />
        public override bool IsSnapshot => true;

        #endregion

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _vssBackupComponents?.Dispose();
            }

            base.Dispose(disposing);
        }

    }
}