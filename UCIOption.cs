namespace Promote;

internal enum OptionType
{
    Check,
    Spin,
    Combo,
    Button,
    String
}

internal interface IUCIType { }

internal class UCIType<T> : IUCIType
{
    public T? Value { get; set; }
    public T? Max { get; set; }
    public T? Min { get; set; }
    public T? Default { get; set; }

    public UCIType(string type)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(type, nameof(type));

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            throw new ArgumentException(Messages.EmptyType, nameof(type));
        }

        for (int i = 1; i < tokens.Length; i += 2)
        {
            if (i + 1 >= tokens.Length)
            {
                throw new ArgumentException(Messages.MalformedType, nameof(type));
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
                    throw new ArgumentException(Utils.GetMessage(Messages.UnknownProperty, prop), nameof(type));
                }
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

    public UCICombo(string type)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(type, nameof(type));

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            throw new ArgumentException(Messages.EmptyType, nameof(type));
        }

        for (int i = 1; i < tokens.Length; i += 2)
        {
            if (i + 1 >= tokens.Length)
            {
                throw new ArgumentException(Messages.MalformedType, nameof(type));
            }

            string prop = tokens[i].ToLowerInvariant();
            string? rawValue = tokens[i + 1];

            if (rawValue == "<empty>") rawValue = null;

            switch (prop)
            {
                case "var":
                {
                    if (rawValue is not null)
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
                    throw new ArgumentException(Utils.GetMessage(Messages.UnknownProperty, prop), nameof(type));
                }
            }
        }

        Value = Default ?? (Options.Count > 0 ? Options[0] : null);
    }
}

internal class UCIButton : IUCIType
{
    public string? Label { get; }

    public UCIButton(string type)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(type, nameof(type));

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            throw new ArgumentException(Messages.EmptyType, nameof(type));
        }

        if (tokens.Length >= 2)
        {
            Label = string.Join(' ', tokens.Skip(1));
        }
    }
}

internal class UCIOption
{
    public string Name { get; }
    public OptionType Type { get; }
    public IUCIType UCIType { get; }

    public UCIOption(string name, string type)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name, nameof(name));
        ArgumentNullException.ThrowIfNullOrEmpty(type, nameof(type));

        string[] tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            throw new ArgumentException(Messages.EmptyType, nameof(type));
        }

        string optionType = tokens[0];

        ArgumentNullException.ThrowIfNullOrEmpty(optionType, nameof(optionType));

        if (!Enum.TryParse<OptionType>(optionType, true, out OptionType parsed))
        {
            throw new ArgumentException(Utils.GetMessage(Messages.InvalidOptionType, optionType), nameof(type));
        }

        Name = name;
        Type = parsed;

        switch (optionType.ToLowerInvariant())
        {
            case "spin":
            {
                UCIType = new UCIType<int>(type);
            }
            break;

            case "check":
            {
                UCIType = new UCIType<bool>(type);
            }
            break;

            case "combo":
            {
                UCIType = new UCICombo(type);
            }
            break;

            case "button":
            {
                UCIType = new UCIButton(type);
            }
            break;

            case "string":
            {
                UCIType = new UCIType<string>(type);
            }
            break;

            default:
            {
                throw new ArgumentException(Utils.GetMessage(Messages.UnsupportedOptionType, optionType), nameof(type));
            }
        }
    }
}