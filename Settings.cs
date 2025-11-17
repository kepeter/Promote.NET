namespace Promote;

public sealed class EngineSettings
{
    public string Path { get; init; } = string.Empty;
    public int Timeout { get; init; } = 5000;
}

public sealed class UISettings
{
    public string Theme { get; init; } = "light";
    public int FontSize { get; init; } = 12;
}

public sealed class Settings
{
    public EngineSettings Engine { get; init; } = new EngineSettings();
    public UISettings UI { get; init; } = new UISettings();
}
