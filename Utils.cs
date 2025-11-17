using System.Globalization;

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
}
