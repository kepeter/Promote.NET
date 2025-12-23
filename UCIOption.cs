using Microsoft.Extensions.Logging;

using static Promote.Utils;

namespace Promote;

internal enum OptionType
{
    Check,
    Spin,
    Combo,
    Button,
    String,
    Unknown = 9999
}

internal interface IUCIType { }

internal class UCIType<T> : IUCIType
{
    public T? Value { get; set; }
    public T? Max { get; set; }
    public T? Min { get; set; }
    public T? Default { get; set; }

    public UCIType(string type, ILogger<UCIType<T>> logger)
    {
        if (string.IsNullOrEmpty(type))
        {
            Log(logger, Messages.Options_EmptyType);
            return;
        }

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            Log(logger, Messages.Options_EmptyType);
            return;
        }

        for (int i = 1; i < tokens.Length; i += 2)
        {
            if (i + 1 >= tokens.Length)
            {
                Log(logger, Messages.Options_MalformedType);
                break;
            }

            string prop = tokens[i].ToLowerInvariant();
            string? rawValue = tokens[i + 1];

            if (rawValue == "<empty>") rawValue = null;

            switch (prop)
            {
                case "default":
                {
                    Default = (T?)Convert.ChangeType(rawValue, typeof(T));
                }
                break;

                case "min":
                {
                    Min = (T?)Convert.ChangeType(rawValue, typeof(T));
                }
                break;

                case "max":
                {
                    Max = (T?)Convert.ChangeType(rawValue, typeof(T));
                }
                break;

                case "var":
                {
                    Value = (T?)Convert.ChangeType(rawValue, typeof(T));
                }
                break;

                default:
                {
                    Log(logger, GetMessage(Messages.Options_UnknownProperty, prop));
                }
                break;
            }
        }

        if (EqualityComparer<T?>.Default.Equals(Value, default))
        {
            Value = Default;
        }
    }
}

internal class UCICombo : IUCIType
{
    public List<string> Options { get; } = new List<string>();
    public string? Value { get; set; }
    public string? Default { get; set; }

    public UCICombo(string type, ILogger<UCICombo> logger)
    {
        if (string.IsNullOrEmpty(type))
        {
            Log(logger, Messages.Options_EmptyType);
            return;
        }

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            Log(logger, Messages.Options_EmptyType);
            return;
        }

        for (int i = 1; i < tokens.Length; i += 2)
        {
            if (i + 1 >= tokens.Length)
            {
                Log(logger, Messages.Options_MalformedType);
                break;
            }

            string prop = tokens[i].ToLowerInvariant();
            string? rawValue = tokens[i + 1];

            if (rawValue == "<empty>") rawValue = null;

            switch (prop)
            {
                case "var":
                {
                    if (rawValue != null)
                    {
                        Options.Add(rawValue);
                    }
                }
                break;

                case "default":
                {
                    Default = rawValue;
                }
                break;

                default:
                {
                    Log(logger, GetMessage(Messages.Options_UnknownProperty, prop));
                }
                break;
            }
        }

        Value = Default ?? (Options.Count > 0 ? Options[0] : null);
    }
}

internal class UCIButton : IUCIType
{
    public string? Label { get; }

    public UCIButton(string type, ILogger<UCIButton> logger)
    {
        if (string.IsNullOrEmpty(type))
        {
            Log(logger, Messages.Options_EmptyType);
            return;
        }

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            Log(logger, Messages.Options_EmptyType);
            return;
        }

        if (tokens.Length >= 2)
        {
            Label = string.Join(' ', tokens.Skip(1));
        }
    }
}

internal class UCIUnknown : IUCIType
{
    public string ExpectedType { get; }
    public string OriginalMessage { get; }

    public UCIUnknown(string expectedType, string originalMessage, ILogger<UCIUnknown> logger)
    {
        ExpectedType = expectedType;
        OriginalMessage = originalMessage;

        Log(logger, originalMessage);
    }
}

internal class UCIOption
{
    public string? Name { get; }
    public OptionType Type { get; }
    public IUCIType UCIType { get; }

    public UCIOption(string name, string type, ILogger<UCIOption> logger, ILoggerFactory loggerFactory)
    {
        ILogger<UCIUnknown> uciUnknownLogger = loggerFactory.CreateLogger<UCIUnknown>();

        if (string.IsNullOrEmpty(name))
        {
            UCIType = new UCIUnknown(type, Messages.Options_EmptyName, uciUnknownLogger);
            return;
        }

        if (string.IsNullOrEmpty(type))
        {
            UCIType = new UCIUnknown(type, Messages.Options_EmptyType, uciUnknownLogger);
            return;
        }

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            UCIType = new UCIUnknown(type, Messages.Options_EmptyType, uciUnknownLogger);
            return;
        }

        string optionType = tokens[0];

        if(string.IsNullOrEmpty(optionType))
        {
            UCIType = new UCIUnknown(type, Messages.Options_EmptyType, uciUnknownLogger);
            return;
        }

        if (!Enum.TryParse(optionType, true, out OptionType parsed))
        {
            UCIType = new UCIUnknown(type, Messages.Options_InvalidOptionType, uciUnknownLogger);
            return;
        }

        Name = name;
        Type = parsed;

        switch (parsed)
        {
            case OptionType.Spin:
            {
                ILogger<UCIType<int>> uciTypeIntLogger = loggerFactory.CreateLogger<UCIType<int>>();

                UCIType = new UCIType<int>(type, uciTypeIntLogger);
            }
            break;

            case OptionType.Check:
            {
                ILogger<UCIType<bool>> uciTypeBoolLogger = loggerFactory.CreateLogger<UCIType<bool>>();

                UCIType = new UCIType<bool>(type, uciTypeBoolLogger);
            }
            break;

            case OptionType.Combo:
            {
                ILogger<UCICombo> uciComboLogger = loggerFactory.CreateLogger<UCICombo>();

                UCIType = new UCICombo(type, uciComboLogger);
            }
            break;

            case OptionType.Button:
            {
                ILogger<UCIButton> uciButtonLogger = loggerFactory.CreateLogger<UCIButton>();

                UCIType = new UCIButton(type, uciButtonLogger);
            }
            break;

            case OptionType.String:
            {
                ILogger<UCIType<string>> uciTypeStringLogger = loggerFactory.CreateLogger<UCIType<string>>();

                UCIType = new UCIType<string>(type, uciTypeStringLogger);
            }
            break;

            default:
            {
                UCIType = new UCIUnknown(type, Messages.Options_UnsupportedOptionType, uciUnknownLogger);
            }
            break;
        }
    }
}
