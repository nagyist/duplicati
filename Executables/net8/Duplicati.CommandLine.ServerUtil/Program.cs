using System.Threading.Tasks;
using Duplicati.Library.Crashlog;

namespace Duplicati.CommandLine.ServerUtil.Net8
{
    // Wrapper class to keep code independent
    public static class Program
    {
        public static Task<int> Main(string[] args)
            => CrashlogHelper.WrapWithCrashLog(() => Duplicati.CommandLine.ServerUtil.Program.Main(args));
    }
}