// Copyright (C) 2024, The Duplicati Team
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
using System.Linq;
using System.Collections.Generic;
using Duplicati.Library.Common.IO;
using Duplicati.Library.Common;

using System.Security.Cryptography;
using Duplicati.Library.Utility;

namespace Duplicati.CommandLine.RecoveryTool
{
    public static class Restore
    {
        private static readonly ISystemIO systemIO = SystemIO.IO_OS;

        public static int Run(List<string> args, Dictionary<string, string> options, Library.Utility.IFilter filter)
        {
            if (args.Count != 2 && args.Count != 3)
            {
                Console.WriteLine("Invalid argument count ({0} expected 2 or 3): {1}{2}", args.Count, Environment.NewLine, string.Join(Environment.NewLine, args));
                return 100;
            }

            var folder = Path.GetFullPath(args[1]);

            if (!Directory.Exists(folder))
            {
                Console.WriteLine("Folder not found: {0}", folder);
                return 100;
            }

            Directory.SetCurrentDirectory(folder);

            string targetpath;
            options.TryGetValue("targetpath", out targetpath);

            string ixfile;
            options.TryGetValue("indexfile", out ixfile);
            if (string.IsNullOrWhiteSpace(ixfile))
                ixfile = "index.txt";

            ixfile = Path.GetFullPath(ixfile);
            if (!File.Exists(ixfile))
            {
                Console.WriteLine("Index file not found, perhaps you need to run the index command?");
                return 100;
            }


            string filelist;
            if (args.Count == 2)
            {
                var time = List.ParseListFiles(folder).First();
                filelist = time.Value;

                Console.WriteLine("Using set 0 with timestamp {0}", time.Key.ToLocalTime());
            }
            else
            {
                filelist = List.SelectListFile(args[2], folder);
            }

            Library.Main.Volumes.VolumeReaderBase.UpdateOptionsFromManifest(Path.GetExtension(filelist).Trim('.'), filelist, new Duplicati.Library.Main.Options(options));

            string blocksize_str;
            options.TryGetValue("blocksize", out blocksize_str);
            string blockhash_str;
            options.TryGetValue("block-hash-algorithm", out blockhash_str);
            string filehash_str;
            options.TryGetValue("block-hash-algorithm", out filehash_str);

            long blocksize = string.IsNullOrWhiteSpace(blocksize_str) ? 0 : Library.Utility.Sizeparser.ParseSize(blocksize_str);

            if (blocksize <= 0)
            {
                Console.WriteLine("Invalid blocksize: {0}, try setting --blocksize manually", blocksize);
                return 100;
            }

            
            if (string.IsNullOrWhiteSpace(blockhash_str))
                throw new Duplicati.Library.Interface.UserInformationException(string.Format("Block hash algorithm not valid: {0}", blockhash_str), "BlockHashAlgorithmNotSupported");
            if (string.IsNullOrWhiteSpace(filehash_str))
                throw new Duplicati.Library.Interface.UserInformationException(string.Format("File hash algorithm not valid: {0}", filehash_str), "FileHashAlgorithmNotSupported");


            using (var blockhasher = HashFactory.CreateHasher(blockhash_str))
            using (var filehasher = HashFactory.CreateHasher(filehash_str))
            using (var mru = new CompressedFileMRUCache(options))
            {
                var hashesprblock = blocksize / (blockhasher.HashSize / 8);

                Console.WriteLine("Building lookup table for file hashes");
                using (HashLookupHelper lookup = new HashLookupHelper(ixfile, mru, (int) blocksize, blockhasher.HashSize / 8))
                {
                    var filecount = 0L;
                    string largestprefix = null;
                    string[] largestprefixparts = null;

                    if (!string.IsNullOrWhiteSpace(targetpath))
                        Console.WriteLine("Computing restore path");

                    foreach (var f in List.EnumerateFilesInDList(filelist, filter, options))
                    {
                        if (largestprefix == null)
                        {
                            largestprefix = f.Path;
                            largestprefixparts = largestprefix.Split(new char[] { Path.DirectorySeparatorChar });
                        }
                        else if (largestprefix.Length > 1)
                        {
                            var parts = f.Path.Split(new char[] { Path.DirectorySeparatorChar });

                            var ni = 0;
                            for (; ni < Math.Min(parts.Length, largestprefixparts.Length); ni++)
                                if (!Library.Utility.Utility.ClientFilenameStringComparer.Equals(parts[ni], largestprefixparts[ni]))
                                    break;

                            if (ni != largestprefixparts.Length)
                            {
                                if (ni == 0)
                                {
                                    largestprefixparts = new string[0];
                                    largestprefix = string.Empty;
                                }
                                else
                                {
                                    Array.Resize(ref largestprefixparts, ni - 1);
                                    largestprefix = string.Join(Util.DirectorySeparatorString, largestprefixparts);
                                }
                            }
                        }
                        filecount++;
                    }

                    Console.WriteLine("Restoring {0} files to {1}", filecount, string.IsNullOrWhiteSpace(targetpath) ? "original position" : targetpath);

                    if (Platform.IsClientPosix || largestprefix.Length > 0)
                        largestprefix = Util.AppendDirSeparator(largestprefix);

                    if (!string.IsNullOrEmpty(largestprefix))
                        Console.WriteLine("Removing common prefix {0} from files", largestprefix);

                    var i = 0L;
                    foreach (var f in List.EnumerateFilesInDList(filelist, filter, options))
                    {
                        try
                        {
                            var targetfile = MapToRestorePath(f.Path, largestprefix, targetpath);
                            if (!systemIO.DirectoryExists(systemIO.PathGetDirectoryName(targetfile)))
                                systemIO.DirectoryCreate(systemIO.PathGetDirectoryName(targetfile));

                            Console.Write("{0}: {1} ({2})", i, targetfile, Library.Utility.Utility.FormatSizeString(f.Size));

                            using (var tf = new Library.Utility.TempFile())
                            {
                                using (var sw = File.OpenWrite(tf))
                                {
                                    if (f.BlocklistHashes == null)
                                    {
                                        lookup.WriteHash(sw, f.Hash);
                                    }
                                    else
                                    {
                                        var blhi = 0L;
                                        foreach (var blh in f.BlocklistHashes)
                                        {
                                            Console.Write(" {0}", blhi);
                                            var blockhashoffset = blhi * hashesprblock * blocksize;

                                            try
                                            {
                                                var bi = 0;
                                                foreach (var h in lookup.ReadBlocklistHashes(blh))
                                                {
                                                    try
                                                    {
                                                        sw.Position = blockhashoffset + (bi * blocksize);
                                                        lookup.WriteHash(sw, h);
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Console.WriteLine("Failed to read hash: {0}{1}{2}", h, Environment.NewLine, ex);
                                                    }

                                                    bi++;
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine("Failed to read Blocklist hash: {0}{1}{2}", blh, Environment.NewLine, ex);
                                            }

                                            blhi++;
                                        }
                                    }
                                }

                                string fh;
                                using (var fs = File.OpenRead(tf))
                                    fh = Convert.ToBase64String(filehasher.ComputeHash(fs));

                                if (fh == f.Hash)
                                {
                                    Console.WriteLine(" done!");
                                    systemIO.FileCopy(tf, targetfile, true);
                                }
                                else
                                {
                                    Console.Write(" - Restored file hash mismatch");
                                    if (systemIO.FileExists(targetfile))
                                        Console.WriteLine(" - not overwriting existing file: {0}", targetfile);
                                    else
                                        Console.WriteLine(" - restoring file in damaged condition");


                                }
                            }

                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(" error: {0}", ex);
                        }
                        i++;
                    }
                }
            }

            return 0;
        }

        private static string MapToRestorePath(string path, string prefixpath, string restorepath)
        {
            if (string.IsNullOrWhiteSpace(restorepath))
                return path;

            if (string.IsNullOrWhiteSpace(prefixpath))
            {
                //Special case, restoring to new folder, but files are from different drives
                // So we use the format <restore path> / <drive letter> / <source path>
                // To avoid generating paths with a colon
                if (path.Substring(1, 1) == ":")
                    prefixpath = path.Substring(0, 1) + path.Substring(2);

                return systemIO.PathCombine(restorepath, prefixpath);
            }

            return systemIO.PathCombine(restorepath, path.Substring(prefixpath.Length));
        }



        private class HashLookupHelper : IDisposable
        {
            private const int LOOKUP_TABLE_SIZE = 2048;

            private readonly CompressedFileMRUCache m_cache;
            private readonly int m_hashsize;
            private readonly int m_blocksize;
            private Stream m_indexfile;
            private readonly List<string> m_lookup = new List<string>();
            private readonly List<long> m_offsets = new List<long>();
            private readonly byte[] m_linebuf = new byte[128];
            private readonly byte[] m_newline = Environment.NewLine.Select(x => (byte)x).ToArray();

            public HashLookupHelper(string indexfile, CompressedFileMRUCache cache, int blocksize, int hashsize)
            {
                m_cache = cache;
                m_blocksize = blocksize;
                m_hashsize = hashsize;
                m_indexfile = File.OpenRead(indexfile);

                // Build index ....
                var hashes = 0L;
                string prev = null;
                m_indexfile.Position = 0;
                foreach (var n in AllFileLines())
                {
                    if (n.Key != prev)
                    {
                        hashes++;
                        prev = n.Key;
                    }
                }

                Console.WriteLine("Index file has {0} hashes in total", hashes);

                var lookuptablesize = Math.Max(1, Math.Min(LOOKUP_TABLE_SIZE, hashes) - 1);
                var lookupincrements = hashes / lookuptablesize;

                Console.WriteLine("Building lookup table with {0} entries, giving increments of {1}", lookuptablesize, lookupincrements);

                prev = null;
                var prevoff = 0L;
                var hc = 0L;
                m_indexfile.Position = 0;
                foreach (var n in AllFileLines())
                {
                    if (n.Key != prev)
                    {
                        if ((hc % lookupincrements) == 0)
                        {
                            m_lookup.Add(n.Key);
                            m_offsets.Add(prevoff);
                        }

                        hc++;
                        prev = n.Key;
                        prevoff = m_indexfile.Position;
                    }
                }
            }

            private string ReadNextLine()
            {
                var p = m_indexfile.Position;
                var max = m_indexfile.Read(m_linebuf, 0, m_linebuf.Length);
                if (max == 0)
                    return null;

                var lfi = 0;
                for (int i = 0; i < max; i++)
                    if (m_linebuf[i] == m_newline[lfi])
                    {
                        if (lfi == m_newline.Length - 1)
                        {
                            m_indexfile.Position = p + i + 1;
                            return System.Text.Encoding.Default.GetString(m_linebuf, 0, i - m_newline.Length + 1);

                        }
                        else
                        {
                            lfi++;
                        }
                    }
                    else
                    {
                        lfi = 0;
                    }

                throw new Exception(string.Format("Unexpected long line starting at offset {0}, read {1} bytes without a newline", p, m_linebuf.Length));
            }

            private IEnumerable<KeyValuePair<string, string>> AllFileLines()
            {
                while (true)
                {
                    var str = ReadNextLine();
                    if (str == null)
                        break;

                    if (str.Length == 0)
                        continue;

                    var ix = str.IndexOf(", ", StringComparison.Ordinal);
                    if (ix < 0)
                        Console.WriteLine("Failed to parse line starting at offset {0} in index file, string: {1}", m_indexfile.Position - str.Length - m_newline.Length, str);
                    yield return new KeyValuePair<string, string>(str.Substring(0, ix), str.Substring(ix + 2));
                }
            }

            public IEnumerable<string> ReadBlocklistHashes(string hash)
            {
                var bytes = ReadHash(hash);
                for (var i = 0; i < bytes.Length; i += m_hashsize)
                    yield return Convert.ToBase64String(bytes, i, m_hashsize);
            }

            public byte[] ReadHash(string hash)
            {
                var ix = m_lookup.BinarySearch(hash, StringComparer.Ordinal);
                if (ix < 0)
                {
                    ix = ~ix;
                    if (ix != 0)
                        ix--;
                }

                m_indexfile.Position = m_offsets[ix];

                foreach (var v in AllFileLines())
                {
                    if (v.Key == hash)
                        using (var fs = m_cache.ReadBlock(v.Value, hash))
                        {
                            var buf = new byte[m_blocksize];
                            var l = Duplicati.Library.Utility.Utility.ForceStreamRead(fs, buf, buf.Length);
                            Array.Resize(ref buf, l);

                            return buf;
                        }


                    if (StringComparer.Ordinal.Compare(hash, v.Key) < 0)
                        break;
                }

                throw new Exception(string.Format("Unable to locate block with hash: {0}", hash));
            }

            public void WriteHash(Stream sw, string hash)
            {
                var h = ReadHash(hash);
                sw.Write(h, 0, h.Length);
            }


            public void Dispose()
            {
                if (m_indexfile != null)
                    m_indexfile.Dispose();
                m_indexfile = null;
            }
        }

        private class CompressedFileMRUCache : IDisposable
        {
            private Dictionary<string, Library.Interface.ICompression> m_lookup = new Dictionary<string, Duplicati.Library.Interface.ICompression>();
            private Dictionary<string, Stream> m_streams = new Dictionary<string, Stream>();
            private List<string> m_mru = new List<string>();
            private readonly Dictionary<string, string> m_options;

            private const int MAX_OPEN_ARCHIVES = 20;

            public CompressedFileMRUCache(Dictionary<string, string> options)
            {
                m_options = options;
            }

            public Stream ReadBlock(string filename, string hash)
            {
                Library.Interface.ICompression cf;
                Stream stream;
                if (!m_lookup.TryGetValue(filename, out cf) || !m_streams.TryGetValue(filename, out stream))
                {
                    stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    cf = Library.DynamicLoader.CompressionLoader.GetModule(Path.GetExtension(filename).Trim('.'), stream, Library.Interface.ArchiveMode.Read, m_options);
                    if (cf == null)
                    {
                        stream.Dispose();
                        throw new Exception(string.Format("Unable to decompress {0}, no such compression module {1}", filename, Path.GetExtension(filename).Trim('.')));
                    }
                    m_lookup[filename] = cf;
                    m_streams[filename] = stream;
                }

                if (m_mru.Count > 0 && m_mru[0] != filename)
                {
                    m_mru.Remove(filename);
                    m_mru.Insert(0, filename);
                }

                while (m_mru.Count > MAX_OPEN_ARCHIVES)
                {
                    var f = m_mru.Last();
                    m_lookup[f].Dispose();
                    m_lookup.Remove(f);
                    m_streams[f].Dispose();
                    m_streams.Remove(f);
                    m_mru.Remove(f);
                }

                return cf.OpenRead(Library.Utility.Utility.Base64PlainToBase64Url(hash));
            }

            #region IDisposable implementation

            public void Dispose()
            {
                foreach (var v in m_lookup.Values)
                    try { v.Dispose(); }
                    catch { }

                foreach (var v in m_streams.Values)
                    try { v.Dispose(); }
                    catch { }

                m_lookup = null;
                m_streams = null;
                m_mru = null;
            }

            #endregion
        }
    }
}

