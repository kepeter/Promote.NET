namespace Promote;

internal sealed class EngineSettings
{
    public string Path { get; init; } = string.Empty;
    public int Timeout { get; init; } = 5000;
}

internal sealed class BoardSettings
{
    public string Theme { get; init; } = "light";
    public int FontSize { get; init; } = 12;
}

internal sealed class Settings
{
    public EngineSettings Engine { get; init; } = new EngineSettings();
    public BoardSettings Board { get; init; } = new BoardSettings();
}
