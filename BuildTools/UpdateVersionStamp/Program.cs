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
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UpdateVersionStamp
{
    public static class Program
    {
        private static readonly string DIR_SEP = Path.DirectorySeparatorChar.ToString();
        private static readonly Dictionary<string, Regex> FILEMAP;
        
        static Program()
        {
            var versionre = @"(?<version>\d+\.\d+\.(\*|(\d+(\.(\*|\d+)))?))";
            FILEMAP = new Dictionary<string, Regex>(StringComparer.InvariantCultureIgnoreCase);
            FILEMAP.Add("UpgradeData.wxi", new Regex(@"\<\?define ProductVersion\=\""" + versionre + @"\"" \?\>"));
            FILEMAP.Add("AssemblyRedirects.xml", new Regex(@"newVersion\=\""" + versionre + @"\"""));
            FILEMAP.Add("index.html", new Regex(@"\?v\=" + versionre));
            FILEMAP.Add("login.html", new Regex(@"\?v\=" + versionre));
            FILEMAP.Add("app.js", new Regex(@"\?v\=" + versionre));
        }
        
        private class Options
        {
            public string sourcefolder = Path.GetFullPath(Environment.CurrentDirectory);
            public string ignorefilter = null;
            public string version = null;
            public string versiontag = null;
            
            public void Fixup()
            {
                sourcefolder = Duplicati.Library.Common.IO.Util.AppendDirSeparator(System.IO.Path.GetFullPath(sourcefolder.Replace("/", DIR_SEP)));
                if (ignorefilter != null)
                    ignorefilter =ignorefilter.Replace("/", DIR_SEP);
            }
        }        
        
        public static void Main(string[] _args)
        {
            List<string> args = new List<string>(_args);
            Dictionary<string, string> options = Duplicati.Library.Utility.CommandLineParser.ExtractOptions(args);
            Options opt = new Options();
            
            foreach (FieldInfo fi in opt.GetType().GetFields())
                if (options.ContainsKey(fi.Name))
                    fi.SetValue(opt, options[fi.Name]);
            
            opt.Fixup();            
            
            Duplicati.Library.Utility.IFilter filter = null;
            if (!string.IsNullOrEmpty(opt.ignorefilter))
                filter = new Duplicati.Library.Utility.FilterExpression(opt.ignorefilter, false);
            
            Func<string, bool> isFile = (string x) => !x.EndsWith(DIR_SEP);
            
            var paths = Duplicati.Library.Utility.Utility.EnumerateFileSystemEntries(opt.sourcefolder)
                .Where(x => Duplicati.Library.Utility.FilterExpression.Matches(filter, x))
                .Where(x => isFile(x) && FILEMAP.ContainsKey(Path.GetFileName(x)))
                .Select(x =>
            {
                var m = FILEMAP[Path.GetFileName(x)].Match(File.ReadAllText(x));
                return m.Success ? 
                        new { File = x, Version = new Version(m.Groups["version"].Value.Replace("*", "0")), Display = m.Groups["version"].Value } 
                        : null;
            })
                .Where(x => x != null)
                .ToArray(); //No need to re-eval
            
            if (paths.Count() == 0)
            {
                Console.WriteLine("No files found to update...");
                return;
            }
            
            foreach (var p in paths)
                Console.WriteLine("{0}\t:{1}", p.Display, p.File);
            
            if (string.IsNullOrWhiteSpace(opt.version))
            {
                var maxv = paths.Select(x => x.Version).Max();
                opt.version = new Version(
                    maxv.Major,
                    maxv.Minor,
                    maxv.Build,
                    maxv.Revision).ToString();
            }
            
            //Sanity check
            var nv = new Version(opt.version).ToString(4);

            foreach (var p in paths)
            {
                var re = FILEMAP[Path.GetFileName(p.File)];
                var txt = File.ReadAllText(p.File);
                //var m = re.Match(txt).Groups["version"];
                txt = re.Replace(txt, (m) => {
                    var t = m.Groups["version"];
                    return m.Value.Replace(t.Value, nv);
                });
                File.WriteAllText(p.File, txt);
            }

            Console.WriteLine("Updated {0} files to version {1}", paths.Count(), opt.version);
        }
    }
}
