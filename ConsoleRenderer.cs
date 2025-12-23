using Microsoft.Extensions.Options;

namespace Promote;

internal class ConsoleRenderer
{
    private readonly Board _board;
    private readonly Engine _engine;

    private readonly BoardSettings _boardOptions;

    public ConsoleRenderer(Board board, Engine engine, IOptions<Settings>? options = null)
    {
        _board = board;
        _engine = engine;

        _boardOptions = options?.Value.Board ?? new BoardSettings();

        _board.SetPromotionCallback(ChoosePromotion);
    }

    public Piece ChoosePromotion(string from, string to)
    {
        int file = from[0] - 'a';
        int rank = 8 - (from[1] - '0');
        char pieceChar = _board[rank, file];
        bool isWhite = char.IsUpper(pieceChar);

        Console.WriteLine($"Pawn promotion from {from} to {to}. Choose piece: (q)ueen, (r)ook, (b)ishop, k(n)ight. Default: queen");
        Console.Write("> ");
        string? input = (Console.In.ReadLine())?.Trim().ToLowerInvariant();

        return input switch
        {
            "r" => isWhite ? Piece.WhiteRook : Piece.BlackRook,
            "b" => isWhite ? Piece.WhiteBishop : Piece.BlackBishop,
            "n" => isWhite ? Piece.WhiteKnight : Piece.BlackKnight,
            _ => isWhite ? Piece.WhiteQueen : Piece.BlackQueen,
        };
    }

    public async Task Run()
    {
        await _engine.Start().ConfigureAwait(false);
        await _engine.NewGame().ConfigureAwait(false);
        await _engine.PositionFromMoves(null).ConfigureAwait(false);

        Console.Clear();

        await RenderBoard();

        int promptRow = _boardOptions.CellHeight * 8 + 6;
        Console.SetCursorPosition(0, promptRow);

        ShowShortHelp();

        BestMoveResult? opening = await _engine.GetBestMove().ConfigureAwait(false);
        
        if (opening is not null && !string.IsNullOrEmpty(opening.Move))
        {
            WriteMessage($"Opening suggestion\t-> {opening.Move}", promptRow);
        }

        while (true)
        {
            Console.SetCursorPosition(0, promptRow);
            Console.Write(new string(' ', Console.WindowWidth)); // clear line
            Console.SetCursorPosition(0, promptRow);
            Console.Write("> ");
            
            string? raw = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string input = raw.Trim();
            string cmd = input.ToLowerInvariant();

            if (cmd == "quit" || cmd == "q")
            {
                break;
            }

            if (cmd == "help" || cmd == "?")
            {
                ShowHelp(promptRow);
                continue;
            }

            if (cmd == "undo" || cmd == "u")
            {
                var last = _board.Undo();
                
                if (last is null)
                {
                    WriteMessage("Nothing to undo.", promptRow);
                }
                else
                {
                    await RenderBoard();
                    WriteMessage("Undo applied.", promptRow);
                }

                continue;
            }

            if (cmd == "fen")
            {
                WriteMessage(_board.ToFen(), promptRow);
                continue;
            }

            if (cmd == "reset" || cmd == "r")
            {
                const string startFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

                _board.FromFen(startFen);

                await _engine.NewGame().ConfigureAwait(false);
                await _engine.PositionFromMoves(null).ConfigureAwait(false);

                await RenderBoard();
                WriteMessage("Board reset to starting position.", promptRow);
                continue;
            }

            string from = string.Empty;
            string to = string.Empty;

            if (input.Contains('-'))
            {
                var parts = input.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2) { from = parts[0]; to = parts[1]; }
            }
            else if (input.Contains(','))
            {
                var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2) { from = parts[0]; to = parts[1]; }
            }
            else
            {
                var parts = input.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2) { from = parts[0]; to = parts[1]; }
                else if (input.Length == 4)
                {
                    from = input.Substring(0, 2);
                    to = input.Substring(2, 2);
                }
            }

            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                WriteMessage("Unknown command or invalid move format. Type 'help' for usage.", promptRow);
                continue;
            }

            bool ok = _board.Move(from, to);

            await RenderBoard();
            WriteMessage($"You\t-> {from}{to}", promptRow);

            if (ok)
            {
                ok = await _engine.PositionFromMoves(_board.GetUciMoves()).ConfigureAwait(false);

                if (ok)
                {
                    BestMoveResult? best = await _engine.GetBestMove().ConfigureAwait(false);

                    if (best is not null && !string.IsNullOrEmpty(best.Move))
                    {
                        bool applied = ApplyEngineMove(best.Move);

                        if (applied)
                        {
                            await _engine.PositionFromMoves(_board.GetUciMoves()).ConfigureAwait(false);

                            await RenderBoard();

                            string scoreText = best.ScoreCp is not null ? $"cp {best.ScoreCp}" : best.ScoreMate is not null ? $"mate {best.ScoreMate}" : "n/a";
                            string ponderText = string.IsNullOrEmpty(best.Ponder) ? string.Empty : $" ponder {best.Ponder}";

                            WriteMessage($"Engine\t-> {best.Move} ({scoreText}){ponderText}", promptRow);
                        }
                        else
                        {
                            WriteMessage($"Engine move invalid or failed: {best.Move}", promptRow);
                        }
                    }
                    else
                    {
                        WriteMessage("Engine did not return a move.", promptRow);
                    }
                }
            }
            else
            {
                WriteMessage($"Invalid move: {from} -> {to}", promptRow);
            }
        }

        Console.SetCursorPosition(0, _boardOptions.CellHeight * 8 + 8);
        Console.WriteLine("Exiting...");
    }

    private bool ApplyEngineMove(string engineMove)
    {
        if (string.IsNullOrEmpty(engineMove) || engineMove.Length < 4) return false;

        string from = engineMove.Substring(0, 2);
        string to = engineMove.Substring(2, 2);
        char? promoChar = null;

        if (engineMove.Length >= 5)
        {
            promoChar = engineMove[4];
        }

        bool usedTempCallback = false;

        if (promoChar.HasValue)
        {
            int file = from[0] - 'a';
            int rank = 8 - (from[1] - '0');
            char pieceChar = _board[rank, file];
            bool isWhite = char.IsUpper(pieceChar);

            Piece promotedPiece = PromoCharToPiece(promoChar.Value, isWhite);

            _board.SetPromotionCallback((f, t) => promotedPiece);
            usedTempCallback = true;
        }

        bool ok = _board.Move(from, to);

        if (usedTempCallback)
        {
            _board.SetPromotionCallback(ChoosePromotion);
        }

        return ok;
    }

    private static Piece PromoCharToPiece(char c, bool isWhite)
    {
        return c switch
        {
            'r' or 'R' => isWhite ? Piece.WhiteRook : Piece.BlackRook,
            'b' or 'B' => isWhite ? Piece.WhiteBishop : Piece.BlackBishop,
            'n' or 'N' => isWhite ? Piece.WhiteKnight : Piece.BlackKnight,
            _ => isWhite ? Piece.WhiteQueen : Piece.BlackQueen,
        };
    }

    private void ShowShortHelp()
    {
        int r = _boardOptions.CellHeight * 8 + 5;

        Console.SetCursorPosition(0, r);
        Console.WriteLine("Commands: <from> <to> (e2 e4), undo (u), fen, reset (r), help (?), quit (q)");
    }

    private void ShowHelp(int promptRow)
    {
        int helpRow = promptRow + 2;

        Console.SetCursorPosition(0, helpRow);
        Console.WriteLine("Enter moves using algebraic squares, e.g. 'e2 e4' or 'e2-e4' or 'e2e4'.");
        Console.WriteLine("Commands:");
        Console.WriteLine("  undo  | u    - undo last move");
        Console.WriteLine("  fen          - print current FEN");
        Console.WriteLine("  reset | r    - reset board to starting position");
        Console.WriteLine("  help  | ?    - show this help");
        Console.WriteLine("  quit  | q    - exit");
    }

    private void WriteMessage(string message, int promptRow)
    {
        int msgRow = promptRow + 2;

        Console.SetCursorPosition(0, msgRow);
        Console.Write(new string(' ', Console.WindowWidth));
        Console.SetCursorPosition(0, msgRow);
        Console.WriteLine(message);
    }

    private async Task RenderBoard()
    {
        TextWriter writer = Console.Out;

        Console.SetCursorPosition(0, 0);

        ConsoleColor origBg = Console.BackgroundColor;
        ConsoleColor origFg = Console.ForegroundColor;

        ConsoleColor lightSquare = _boardOptions.LightSquare;
        ConsoleColor darkSquare = _boardOptions.DarkSquare;
        ConsoleColor whitePiece = _boardOptions.WhitePiece;
        ConsoleColor blackPiece = _boardOptions.BlackPiece;

        int cellWidth = _boardOptions.CellWidth;
        int cellHeight = _boardOptions.CellHeight;

        int leftPadding = (cellWidth - 1) / 2;
        int rightPadding = cellWidth - leftPadding - 1;

        const int rankLabelWidth = 4;

        int totalBoardContentWidth = rankLabelWidth + (8 * cellWidth);

        int outerSpaces = 0;
        try
        {
            outerSpaces = Math.Max(0, (Console.WindowWidth - totalBoardContentWidth) / 2);
        }
        catch
        {
            outerSpaces = 8;
        }

        string outerMargin = new string(' ', outerSpaces);
        string rankLabelPlaceholder = new string(' ', rankLabelWidth);

        await writer.WriteAsync(outerMargin);
        await writer.WriteAsync(rankLabelPlaceholder);
        for (int c = 0; c < 8; c++)
        {
            await writer.WriteAsync(new string(' ', leftPadding));
            await writer.WriteAsync((char)('a' + c));
            await writer.WriteAsync(new string(' ', rightPadding));
        }
        await writer.WriteLineAsync();
        await writer.WriteLineAsync();

        for (int r = 0; r < 8; r++)
        {
            for (int innerRow = 0; innerRow < cellHeight; innerRow++)
            {
                await writer.WriteAsync(outerMargin);

                if (innerRow == cellHeight / 2)
                {
                    await writer.WriteAsync($" {8 - r}  ");
                }
                else
                {
                    await writer.WriteAsync(rankLabelPlaceholder);
                }

                for (int c = 0; c < 8; c++)
                {
                    char p = _board[r, c];

                    bool isLight = (r + c) % 2 == 0;
                    bool isWhite = char.IsUpper(p);

                    char pieceCenter = p;

                    Console.BackgroundColor = isLight ? lightSquare : darkSquare;
                    Console.ForegroundColor = isWhite ? whitePiece : blackPiece;

                    if (innerRow == cellHeight / 2)
                    {
                        await writer.WriteAsync(new string(' ', leftPadding));
                        await writer.WriteAsync(pieceCenter);
                        await writer.WriteAsync(new string(' ', rightPadding));
                    }
                    else
                    {
                        await writer.WriteAsync(new string(' ', cellWidth));
                    }
                }

                Console.BackgroundColor = origBg;
                Console.ForegroundColor = origFg;

                if (innerRow == cellHeight / 2)
                {
                    await writer.WriteAsync($"  {8 - r}");
                }

                await writer.WriteLineAsync();
            }
        }

        await writer.WriteLineAsync();

        await writer.WriteAsync(outerMargin);
        await writer.WriteAsync(rankLabelPlaceholder);
        for (int c = 0; c < 8; c++)
        {
            await writer.WriteAsync(new string(' ', leftPadding));
            await writer.WriteAsync((char)('a' + c));
            await writer.WriteAsync(new string(' ', rightPadding));
        }

        Console.BackgroundColor = origBg;
        Console.ForegroundColor = origFg;
    }
}
