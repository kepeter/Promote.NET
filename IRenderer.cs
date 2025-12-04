namespace Promote;

internal interface IRenderer
{
    Engine Engine { get; }
    Board Board { get; }

    Piece ChoosePromotion(string from, string to);
    Task RenderBoard(Piece[,] board);
    Task ShowMessage(string message);
}
