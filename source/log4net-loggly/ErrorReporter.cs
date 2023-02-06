using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace log4net.loggly
{
    internal static class ErrorReporter
    {
        public static void ReportError(string error)
        {
            Trace.WriteLine(error);
            Console.WriteLine(error);
        }
        
        public static void Dump(string messages)
        {
            Trace.WriteLine(messages);
            Console.WriteLine(messages);
        }
    }
}
