namespace Promote;

internal sealed class EngineSettings
{
    public string Path { get; init; } = string.Empty;
    public int Timeout { get; init; } = 5000;
}

internal sealed class BoardSettings
{
    public int CellWidth { get; init; } = 7;
    public int CellHeight { get; init; } = 3;

    public ConsoleColor LightSquare { get; init; } = ConsoleColor.White;
    public ConsoleColor DarkSquare { get; init; } = ConsoleColor.Black;

    public ConsoleColor WhitePiece { get; init; } = ConsoleColor.Green;
    public ConsoleColor BlackPiece { get; init; } = ConsoleColor.Blue;

    public int FontSize { get; init; } = 12;
}

internal sealed class Settings
{
    public EngineSettings Engine { get; init; } = new EngineSettings();
    public BoardSettings Board { get; init; } = new BoardSettings();
}
