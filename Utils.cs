using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

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

    public static void Log<T>(ILogger<T>? logger, string message, Exception? ex = null, [CallerMemberName] string memberName = "", [CallerLineNumber] int lineNumber = 0)
    {
        string typeName = typeof(T).FullName ?? "<unknown>";
        string template = "{Message} (at {Type}.{Member}:{Line})";

        if (logger != null)
        {
            if (ex != null)
            {
                logger.LogError(ex, template, message, typeName, memberName, lineNumber);
            }
            else
            {
                logger.LogInformation(template, message, typeName, memberName, lineNumber);
            }
        }
        else
        {
            if (ex != null)
            {
                Console.WriteLine(FormatException(ex));
            }

            Console.WriteLine("{0} (at {1}.{2}:{3})", message, typeName, memberName, lineNumber);
        }
    }

    private static string FormatException(Exception ex)
    {
        var sb = new StringBuilder();
        int level = 0;
        Exception? current = ex;

        while (current != null)
        {
            sb.AppendLine($"Exception[{level}]: {current.GetType().FullName}: {current.Message}");

            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                sb.AppendLine(current.StackTrace);
            }

            current = current.InnerException;

            if (current != null)
            {
                sb.AppendLine("--- Inner Exception ---");
            }

            level++;
        }

        return sb.ToString();
    }
}
