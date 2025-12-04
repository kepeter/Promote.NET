using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

namespace Promote;

internal static class Utils
{
    public static string GetMessage(string resource, params object?[] args)
    {
        if (args == null || args.Length == 0)
        {
            return resource;
        }
        else
        {
            return string.Format(CultureInfo.CurrentUICulture, resource, args);
        }
    }

    public static void Log(ILogger? logger, string message, Exception? ex = null, [CallerMemberName] string memberName = "", [CallerLineNumber] int lineNumber = 0)
    {
        string template = "{Message} (at {Member}:{Line})";

        if (logger is not null)
        {
            if (ex is not null)
            {
                logger.LogError(ex, template, message, memberName, lineNumber);
            }
            else
            {
                logger.LogInformation(template, message, memberName, lineNumber);
            }
        }
        else
        {
            Console.WriteLine("{0} (at {1}:{2})", message, memberName, lineNumber);
        }
    }
}
