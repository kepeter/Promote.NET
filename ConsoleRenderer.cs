using System.Text;

namespace Promote;

internal class ConsoleRenderer : IRenderer
{
    public Engine Engine { get; }
    public Board Board { get; }

    public ConsoleRenderer(Engine engine, Board board)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Board = board ?? throw new ArgumentNullException(nameof(board));

        Board.SetPromotionCallback(ChoosePromotion);
    }

    public Piece ChoosePromotion(string from, string to)
    {
        Console.WriteLine($"Pawn promotion from {from} to {to}. Choose piece: (q)ueen, (r)ook, (b)ishop, k(n)ight. Default: queen");
        Console.Write("> ");
        string? input = (Console.In.ReadLine())?.Trim().ToLowerInvariant();

        return input switch
        {
            "r" => Piece.WhiteRook,
            "b" => Piece.WhiteBishop,
            "n" => Piece.WhiteKnight,
            _ => Piece.WhiteQueen,
        };
    }

    public async Task RenderBoard(Piece[,] board)
    {
        // Build the full board text and write it asynchronously to the console
        var sb = new StringBuilder(8 * (8 + Environment.NewLine.Length));
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                var p = board[r, c];
                char ch = p == Piece.None ? '.' : (char)p;
                sb.Append(ch);
            }
            sb.AppendLine();
        }

        await Console.Out.WriteAsync(sb.ToString());
    }

    public async Task ShowMessage(string message)
    {
        await Console.Out.WriteLineAsync(message);
    }
}
