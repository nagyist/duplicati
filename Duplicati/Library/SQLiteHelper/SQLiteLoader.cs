﻿// Copyright (C) 2024, The Duplicati Team
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
using System.IO;
using Duplicati.Library.Common;
using Duplicati.Library.Common.IO;

namespace Duplicati.Library.SQLiteHelper
{
    public static class SQLiteLoader
    {
        /// <summary>
        /// The tag used for logging
        /// </summary>
        private static readonly string LOGTAG = Logging.Log.LogTagFromType(typeof(SQLiteLoader));

        /// <summary>
        /// A cached copy of the type
        /// </summary>
        private static Type m_type = null;

        /// <summary>
        /// Helper method with logic to handle opening a database in possibly encrypted format
        /// </summary>
        /// <param name="con">The SQLite connection object</param>
        /// <param name="databasePath">The location of Duplicati's database.</param>
        /// <param name="useDatabaseEncryption">Specify if database is encrypted</param>
        /// <param name="password">Encryption password</param>
        public static void OpenDatabase(System.Data.IDbConnection con, string databasePath, bool useDatabaseEncryption, string password)
        {
            var setPwdMethod = con.GetType().GetMethod("SetPassword", new[] { typeof(string) });
            string attemptedPassword;

            if (!useDatabaseEncryption || string.IsNullOrEmpty(password))
                attemptedPassword = null; //No encryption specified, attempt to open without
            else
                attemptedPassword = password; //Encryption specified, attempt to open with

            if (setPwdMethod != null)
                setPwdMethod.Invoke(con, new object[] { attemptedPassword });

            try
            {
                //Attempt to open in preferred state
                OpenSQLiteFile(con, databasePath);
                TestSQLiteFile(con);
            }
            catch
            {
                try
                {
                    //We can't try anything else without a password
                    if (string.IsNullOrEmpty(password))
                        throw;

                    //Open failed, now try the reverse
                    attemptedPassword = attemptedPassword == null ? password : null;

                    con.Close();
                    if (setPwdMethod != null)
                        setPwdMethod.Invoke(con, new object[] { attemptedPassword });
                    OpenSQLiteFile(con, databasePath);

                    TestSQLiteFile(con);
                }
                catch
                {
                    try { con.Close(); }
                    catch (Exception ex) { Logging.Log.WriteExplicitMessage(LOGTAG, "OpenDatabaseFailed", ex, "Failed to open the SQLite database: {0}", databasePath); }
                }

                //If the db is not open now, it won't open
                if (con.State != System.Data.ConnectionState.Open)
                    throw; //Report original error

                //The open method succeeded with the non-default method, now change the password
                var changePwdMethod = con.GetType().GetMethod("ChangePassword", new[] { typeof(string) });
                changePwdMethod.Invoke(con, new object[] { useDatabaseEncryption ? password : null });
            }
        }

        /// <summary>
        /// Loads an SQLite connection instance and opening the database
        /// </summary>
        /// <returns>The SQLite connection instance.</returns>
        public static System.Data.IDbConnection LoadConnection()
        {
            System.Data.IDbConnection con = null;
            SetEnvironmentVariablesForSQLiteTempDir();

            try
            {
                con = (System.Data.IDbConnection)Activator.CreateInstance(Duplicati.Library.SQLiteHelper.SQLiteLoader.SQLiteConnectionType);
            }
            catch (Exception ex)
            {
                Logging.Log.WriteErrorMessage(LOGTAG, "FailedToLoadConnectionSQLite", ex, "Failed to load connection.");
                DisposeConnection(con);

                throw;
            }

            return con;
        }

        /// <summary>
        /// Loads an SQLite connection instance and opening the database
        /// </summary>
        /// <returns>The SQLite connection instance.</returns>
        /// <param name="targetpath">The optional path to the database.</param>
        public static System.Data.IDbConnection LoadConnection(string targetpath)
        {
            if (string.IsNullOrWhiteSpace(targetpath))
                throw new ArgumentNullException(nameof(targetpath));
                
            System.Data.IDbConnection con = LoadConnection();

            try
            {
                OpenSQLiteFile(con, targetpath);
            }
            catch (Exception ex)
            {
                Logging.Log.WriteErrorMessage(LOGTAG, "FailedToLoadConnectionSQLite", ex, @"Failed to load connection with path '{0}'.", targetpath);
                DisposeConnection(con);

                throw;
            }

	    // set custom Sqlite options
            var opts = Environment.GetEnvironmentVariable("CUSTOMSQLITEOPTIONS_DUPLICATI");
            if (opts != null) {
                var topts = opts.Split(new char[]{';'}, StringSplitOptions.RemoveEmptyEntries);
                if (topts.Length > 0) {
                    using (var cmd = con.CreateCommand()) {
                        foreach (var opt in topts) {
                            Logging.Log.WriteVerboseMessage(LOGTAG, "CustomSQLiteOption", @"Setting custom SQLite option '{0}'.", opt);
                            try
                            {
                                cmd.CommandText = string.Format("pragma {0}", opt);
                                cmd.ExecuteNonQuery();
                            }
			    catch (Exception ex)
                            {
                               Logging.Log.WriteErrorMessage(LOGTAG, "CustomSQLiteOption", ex, @"Error setting custom SQLite option '{0}'.", opt);
                            }
	                }
                    }
                }
            }

            return con;
        }

        /// <summary>
        /// Returns the SQLiteCommand type for the current architecture
        /// </summary>
        public static Type SQLiteConnectionType
        {
            get
            {
                return typeof(System.Data.SQLite.SQLiteConnection);
            }
        }

        /// <summary>
        /// Returns the version string from the SQLite type
        /// </summary>
        public static string SQLiteVersion
        {
            get
            {
                var versionString = SQLiteConnectionType.GetProperty("SQLiteVersion")?.GetValue(null, null) as string;
                if (string.IsNullOrWhiteSpace(versionString))
                {
                    // Support for Microsoft.Data.SQLite
                    // NOTE: Has an issue with ? as position parameters
                    var inst = Activator.CreateInstance(SQLiteConnectionType);
                    versionString = SQLiteConnectionType.GetProperty("ServerVersion")?.GetValue(inst, null) as string;
                }
                return versionString;
            }
        }

        /// <summary>
        /// Set environment variables to be used by SQLite to determine which folder to use for temporary files.
        /// From SQLite's documentation, SQLITE_TMPDIR is used for unix-like systems.
        /// For Windows, TMP and TEMP environment variables are used.
        /// </summary>
        private static void SetEnvironmentVariablesForSQLiteTempDir()
        {
            System.Environment.SetEnvironmentVariable("SQLITE_TMPDIR", Library.Utility.TempFolder.SystemTempPath);
            System.Environment.SetEnvironmentVariable("TMP", Library.Utility.TempFolder.SystemTempPath);
            System.Environment.SetEnvironmentVariable("TEMP", Library.Utility.TempFolder.SystemTempPath);
        }

        /// <summary>
        /// Wrapper to dispose the SQLite connection
        /// </summary>
        /// <param name="con">The connection to close.</param>
        private static void DisposeConnection(System.Data.IDbConnection con)
        {
            if (con != null)
                try { con.Dispose(); }
                catch (Exception ex) { Logging.Log.WriteExplicitMessage(LOGTAG, "ConnectionDisposeError", ex, "Failed to dispose connection"); }
        }

        /// <summary>
        /// Opens the SQLite file in the given connection, creating the file if required
        /// </summary>
        /// <param name="con">The connection to use.</param>
        /// <param name="path">Path to the file to open, which may not exist.</param>
        private static void OpenSQLiteFile(System.Data.IDbConnection con, string path)
        {
            // Check if SQLite database exists before opening a connection to it.
            // This information is used to 'fix' permissions on a newly created file.
            var fileExists = false;
            if (!Platform.IsClientWindows)
                fileExists = File.Exists(path);

            con.ConnectionString = "Data Source=" + path;
            con.Open();

            // If we are non-Windows, make the file only accessible by the current user
            if (!Platform.IsClientWindows && !fileExists)
                SetUnixPermissionUserRWOnly(path);
        }

        /// <summary>
        /// Sets the unix permission user read-write Only.
        /// </summary>
        /// <param name="path">The file to set permissions on.</param>
        /// <remarks> Make sure we do not inline this, as we might eventually load Mono.Posix, which is not present on Windows</remarks>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SetUnixPermissionUserRWOnly(string path)
        {
            var fi = PosixFile.GetUserGroupAndPermissions(path);
            PosixFile.SetUserGroupAndPermissions(
                    path, 
                    fi.UID, 
                    fi.GID, 
                    0x180 /* FilePermissions.S_IRUSR | FilePermissions.S_IWUSR*/
                );
        }

        /// <summary>
        /// Tests the SQLite connection, throwing an exception if the connection does not work
        /// </summary>
        /// <param name="con">The connection to test.</param>
        private static void TestSQLiteFile(System.Data.IDbConnection con)
        {
            // Do a dummy query to make sure we have a working db
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SQLITE_MASTER";
                cmd.ExecuteScalar();
            }
        }
    }
}
