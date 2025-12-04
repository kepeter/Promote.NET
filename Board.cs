using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Promote;

internal enum Piece
{
    None = 0,
    WhitePawn = 'P',
    BlackPawn = 'p',
    WhiteKnight = 'N',
    BlackKnight = 'n',
    WhiteBishop = 'B',
    BlackBishop = 'b',
    WhiteRook = 'R',
    BlackRook = 'r',
    WhiteQueen = 'Q',
    BlackQueen = 'q',
    WhiteKing = 'K',
    BlackKing = 'k'
}

internal enum Castling
{
    WhiteKingSide = 0,
    WhiteQueenSide,
    BlackKingSide,
    BlackQueenSide
}

internal class Move
{
    public Piece Piece { get; set; }
    public int From { get; set; }
    public int To { get; set; }
    public bool Capture { get; set; }
    public Piece Captured { get; set; }
    public bool EnPassant { get; set; }
    public bool KingCastling { get; set; }
    public bool QueenCastling { get; set; }
    public bool Check { get; set; }
    public bool Checkmate { get; set; }
    public bool Promotion { get; set; }
    public Piece PromotedTo { get; set; }
}

internal record GameSnapshot(
    Piece[,] Board,
    bool[] CastlingRight,
    int EnPassant,
    int HalfMove,
    int FullMove,
    bool WhiteTurn
);

internal class Board
{
    private Piece[,] board = new Piece[8, 8];

    private bool[] castlingRight = { true, true, true, true };
    private int halfMove = 0;
    private int fullMove = 1;
    private bool whiteTurn = true;
    private int enPassantSquare = -1;

    private const string noValue = "-";
    private const string startingFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private readonly Stack<Move> moveHistory = new Stack<Move>();
    private readonly Stack<GameSnapshot> snapshotHistory = new Stack<GameSnapshot>();

    private readonly BoardSettings _boardSettings;
    private readonly ILogger<Board>? _logger;

    private Func<string, string, Piece>? promotionCallback;

    private static bool IsWhite(Piece p) => char.IsUpper((char)p);
    private static bool IsBlack(Piece p) => char.IsLower((char)p);

    public Board(IOptions<Settings>? options = null, ILogger<Board>? logger = null)
    {
        _boardSettings = options?.Value.Board ?? new BoardSettings();

        _logger = logger;

        FromFen(startingFen);
    }

    public string ToFen()
    {
        string fen = string.Empty;

        for (int i = 0; i < 8; i++)
        {
            int empty = 0;

            for (int j = 0; j < 8; j++)
            {
                if (board[i, j] == Piece.None)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0)
                    {
                        fen += empty.ToString();
                        empty = 0;
                    }

                    fen += (char)board[i, j];
                }
            }

            if (empty > 0)
            {
                fen += empty.ToString();
            }

            if (i < 7)
            {
                fen += "/";
            }
        }

        fen += " " + (whiteTurn ? "w" : "b");

        string castling = string.Empty;

        if (castlingRight[(int)Castling.WhiteKingSide]) castling += "K";
        if (castlingRight[(int)Castling.WhiteQueenSide]) castling += "Q";
        if (castlingRight[(int)Castling.BlackKingSide]) castling += "k";
        if (castlingRight[(int)Castling.BlackQueenSide]) castling += "q";

        if (string.IsNullOrEmpty(castling))
        {
            castling = noValue;
        }

        fen += " " + castling;

        fen += " " + (enPassantSquare == -1 ? noValue : ToAlgebraic(enPassantSquare));

        fen += " " + halfMove;
        fen += " " + fullMove;

        return fen;
    }

    public void FromFen(string fen)
    {
        if (string.IsNullOrEmpty(fen))
        {
            Log(Messages.Board_InvalidFEN_Empty);
            return;
        }

        string[] parts = fen.Split(' ');

        if (parts.Length != 6)
        {
            Log(Messages.Board_InvalidFEN_PartsCount);
            return;
        }

        string[] rows = parts[0].Split('/');

        if (rows.Length != 8)
        {
            Log(Messages.Board_InvalidFEN_RowsCount);
            return;
        }

        Piece[,] newBoard = new Piece[8, 8];

        for (int i = 0; i < 8; i++)
        {
            int col = 0;

            foreach (char c in rows[i])
            {
                if (char.IsDigit(c))
                {
                    int emptySquares = c - '0';
                    for (int j = 0; j < emptySquares; j++)
                    {
                        if (col >= 8)
                        {
                            Log(Utils.GetMessage(Messages.Board_InvalidFEN_RowOverflow, i));
                            return;
                        }

                        newBoard[i, col] = Piece.None;
                        col++;
                    }
                }
                else
                {
                    if (!TryParsePiece(c, out Piece p))
                    {
                        Log(Utils.GetMessage(Messages.Board_InvalidFEN_InvalidPiece, (char)p, c, i));
                        return;
                    }

                    if (col >= 8)
                    {
                        Log(Utils.GetMessage(Messages.Board_InvalidFEN_RowOverflow, i));
                        return;
                    }

                    newBoard[i, col] = p;
                    col++;
                }
            }

            if (col != 8)
            {
                Log(Utils.GetMessage(Messages.Board_InvalidFEN_InvalidRowSquares, i, col));
                return;
            }
        }

        bool newWhiteTurn = parts[1] == "w";

        bool[] newCastling = new bool[4];

        newCastling[(int)Castling.WhiteKingSide] = parts[2].Contains('K');
        newCastling[(int)Castling.WhiteQueenSide] = parts[2].Contains('Q');
        newCastling[(int)Castling.BlackKingSide] = parts[2].Contains('k');
        newCastling[(int)Castling.BlackQueenSide] = parts[2].Contains('q');

        int newEnPassant = -1;

        if (parts[3] == noValue)
        {
            newEnPassant = -1;
        }
        else
        {
            string ep = parts[3];

            if (ep.Length != 2 || ep[0] < 'a' || ep[0] > 'h' || ep[1] < '1' || ep[1] > '8')
            {
                Log(Utils.GetMessage(Messages.Board_InvalidFEN_InvalidEP, ep));
                return;
            }

            newEnPassant = (ep[0] - 'a') + (8 - (ep[1] - '0')) * 8;
        }

        if (!int.TryParse(parts[4], out int newHalfMove))
        {
            Log(Utils.GetMessage(Messages.Board_InvalidFEN_InvalidHalfMove, parts[4]));
            return;
        }

        if (!int.TryParse(parts[5], out int newFullMove))
        {
            Log(Utils.GetMessage(Messages.Board_InvalidFEN_InvalidFullMove, parts[5]));
            return;
        }

        board = newBoard;
        castlingRight = newCastling;
        whiteTurn = newWhiteTurn;
        enPassantSquare = newEnPassant;
        halfMove = newHalfMove;
        fullMove = newFullMove;

        moveHistory.Clear();
        snapshotHistory.Clear();
    }

    public void SetPromotionCallback(Func<string, string, Piece> callback)
    {
        promotionCallback = callback;
    }

    public bool Move(string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        {
            Log(Messages.Board_InvalidMove_NullSquare);
            return false;
        }

        from = from.ToLower();
        to = to.ToLower();

        if (from == to)
        {
            Log(Messages.Board_InvalidMove_SameSquare);
            return false;
        }

        int fromPos = FromAlgebraic(from);
        int toPos = FromAlgebraic(to);

        if (fromPos == -1)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove_InvalidPosition, from));
            return false;
        }

        if (toPos == -1)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove_InvalidPosition, to));
            return false;
        }

        Piece[,] boardBackup = (Piece[,])board.Clone();
        bool[] castlingBackup = (bool[])castlingRight.Clone();
        int enPassantBackup = enPassantSquare;
        int halfMoveBackup = halfMove;
        int fullMoveBackup = fullMove;
        bool whiteTurnBackup = whiteTurn;

        Piece piece = board[fromPos / 8, fromPos % 8];

        if (piece == Piece.None)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove_EmptyPosition, from));
            return false;
        }

        bool isWhite = IsWhite(piece);

        if (isWhite != whiteTurn)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove_InvalidPiece, from));
            return false;
        }

        Piece targetPiece = board[toPos / 8, toPos % 8];

        if (!IsValidMove(piece, targetPiece, fromPos, toPos))
        {
            return false;
        }

        int fromRow = fromPos / 8;
        int fromCol = fromPos % 8;
        int toRow = toPos / 8;
        int toCol = toPos % 8;

        bool pawnMove = (piece == Piece.WhitePawn || piece == Piece.BlackPawn);
        bool capture = targetPiece != Piece.None;

        if (pawnMove && toPos == enPassantSquare && targetPiece == Piece.None)
        {
            int dir = isWhite ? -1 : 1;
            int capturedRow = toRow - dir;

            board[capturedRow, toCol] = Piece.None;
            capture = true;
        }

        board[toRow, toCol] = piece;
        board[fromRow, fromCol] = Piece.None;

        if (piece == Piece.WhiteKing || piece == Piece.BlackKing)
        {
            if (piece == Piece.WhiteKing)
            {
                castlingRight[(int)Castling.WhiteKingSide] = false;
                castlingRight[(int)Castling.WhiteQueenSide] = false;
            }
            else
            {
                castlingRight[(int)Castling.BlackKingSide] = false;
                castlingRight[(int)Castling.BlackQueenSide] = false;
            }

            if (Math.Abs(toCol - fromCol) == 2)
            {
                if (toCol > fromCol)
                {
                    MoveRookForCastling(fromRow, 7, toCol - 1);
                }
                else
                {
                    MoveRookForCastling(fromRow, 0, toCol + 1);
                }
            }
        }

        if (piece == Piece.WhiteRook)
        {
            if (fromRow == 7 && fromCol == 0) castlingRight[(int)Castling.WhiteQueenSide] = false;
            if (fromRow == 7 && fromCol == 7) castlingRight[(int)Castling.WhiteKingSide] = false;
        }
        if (piece == Piece.BlackRook)
        {
            if (fromRow == 0 && fromCol == 0) castlingRight[(int)Castling.BlackQueenSide] = false;
            if (fromRow == 0 && fromCol == 7) castlingRight[(int)Castling.BlackKingSide] = false;
        }

        if (targetPiece == Piece.WhiteRook)
        {
            if (toRow == 7 && toCol == 0) castlingRight[(int)Castling.WhiteQueenSide] = false;
            if (toRow == 7 && toCol == 7) castlingRight[(int)Castling.WhiteKingSide] = false;
        }
        if (targetPiece == Piece.BlackRook)
        {
            if (toRow == 0 && toCol == 0) castlingRight[(int)Castling.BlackQueenSide] = false;
            if (toRow == 0 && toCol == 7) castlingRight[(int)Castling.BlackKingSide] = false;
        }

        Piece promotedPiece = Piece.None;

        if (piece == Piece.WhitePawn && toRow == 0)
        {
            promotedPiece = ChoosePromotion(from, to, true);

            board[toRow, toCol] = promotedPiece;
        }
        else if (piece == Piece.BlackPawn && toRow == 7)
        {
            promotedPiece = ChoosePromotion(from, to, false);

            board[toRow, toCol] = promotedPiece;
        }

        int kingPos = FindPiece(isWhite ? Piece.WhiteKing : Piece.BlackKing);

        if (kingPos == -1)
        {
            RestoreBackup(boardBackup, castlingBackup, enPassantBackup, halfMoveBackup, fullMoveBackup, whiteTurnBackup);

            Log(Utils.GetMessage(Messages.Board_InvalidState_KingMissing, isWhite ? Messages.Board_Color_White : Messages.Board_Color_Black));
            return false;
        }

        int kingRow = kingPos / 8;
        int kingCol = kingPos % 8;

        if (IsPositionUnderAttack(kingRow, kingCol, !isWhite))
        {
            RestoreBackup(boardBackup, castlingBackup, enPassantBackup, halfMoveBackup, fullMoveBackup, whiteTurnBackup);

            Log(Utils.GetMessage(Messages.Board_InvalidMove_KingUnderAttack, from, to));
            return false;
        }

        if (pawnMove && Math.Abs(toRow - fromRow) == 2)
        {
            int dir = isWhite ? -1 : 1;
            int passedRow = fromRow + dir;

            enPassantSquare = passedRow * 8 + fromCol;
        }
        else
        {
            enPassantSquare = -1;
        }

        if (pawnMove || capture)
        {
            halfMove = 0;
        }
        else
        {
            halfMove++;
        }

        whiteTurn = !whiteTurn;

        if (whiteTurn)
        {
            fullMove++;
        }

        snapshotHistory.Push(new GameSnapshot(boardBackup, (bool[])castlingBackup.Clone(), enPassantBackup, halfMoveBackup, fullMoveBackup, whiteTurnBackup));

        var moveRecord = new Move
        {
            Piece = piece,
            From = fromPos,
            To = toPos,
            Capture = capture,
            EnPassant = pawnMove && toPos == enPassantBackup && targetPiece == Piece.None,
            KingCastling = (piece == Piece.WhiteKing || piece == Piece.BlackKing) && Math.Abs(toCol - fromCol) == 2 && toCol == 6,
            QueenCastling = (piece == Piece.WhiteKing || piece == Piece.BlackKing) && Math.Abs(toCol - fromCol) == 2 && toCol == 2,
            Promotion = promotedPiece != Piece.None,
            PromotedTo = promotedPiece
        };

        if (moveRecord.EnPassant)
        {
            int dir = isWhite ? -1 : 1;
            int capturedRow = toRow - dir;
            moveRecord.Captured = boardBackup[capturedRow, toCol];
        }
        else if (moveRecord.Capture)
        {
            moveRecord.Captured = targetPiece;
        }

        int opponentKingPos = FindPiece(whiteTurn ? Piece.WhiteKing : Piece.BlackKing);

        moveRecord.Check = opponentKingPos != -1 && IsPositionUnderAttack(opponentKingPos / 8, opponentKingPos % 8, !whiteTurn);
        moveRecord.Checkmate = moveRecord.Check && !HasLegalMoves(whiteTurn);

        moveHistory.Push(moveRecord);

        return true;
    }

    public Move? Undo()
    {
        if (snapshotHistory.Count == 0) return null;

        var snapshot = snapshotHistory.Pop();

        board = (Piece[,])snapshot.Board.Clone();
        castlingRight = (bool[])snapshot.CastlingRight.Clone();
        enPassantSquare = snapshot.EnPassant;
        halfMove = snapshot.HalfMove;
        fullMove = snapshot.FullMove;
        whiteTurn = snapshot.WhiteTurn;

        Move? lastMove = null;
        if (moveHistory.Count > 0) lastMove = moveHistory.Pop();

        return lastMove;
    }

    private bool IsValidMove(Piece piece, Piece targetPiece, int fromPos, int toPos)
    {
        int fromRow = fromPos / 8;
        int fromCol = fromPos % 8;
        int toRow = toPos / 8;
        int toCol = toPos % 8;

        if (SameColor(piece, targetPiece))
        {
            Log(Messages.Board_InvalidMove_SameColor);
            return false;
        }

        bool isWhite = IsWhite(piece);

        switch (piece)
        {
            case Piece.WhitePawn:
            case Piece.BlackPawn:
            {
                return IsValidPawnMove(fromRow, fromCol, toRow, toCol, isWhite);
            }

            case Piece.WhiteKnight:
            case Piece.BlackKnight:
            {
                return IsValidKnightMove(fromRow, fromCol, toRow, toCol, isWhite);
            }

            case Piece.WhiteBishop:
            case Piece.BlackBishop:
            {
                return IsValidBishopMove(fromRow, fromCol, toRow, toCol, isWhite);
            }

            case Piece.WhiteRook:
            case Piece.BlackRook:
            {
                return IsValidRookMove(fromRow, fromCol, toRow, toCol, isWhite);
            }

            case Piece.WhiteQueen:
            case Piece.BlackQueen:
            {
                return IsValidQueenMove(fromRow, fromCol, toRow, toCol, isWhite);
            }

            case Piece.WhiteKing:
            case Piece.BlackKing:
            {
                return IsValidKingMove(fromRow, fromCol, toRow, toCol, isWhite);
            }
        }

        Log(Messages.Board_InvalidState_UnknownPiece);
        return false;
    }

    private bool IsValidPawnMove(int fromRow, int fromCol, int toRow, int toCol, bool isWhite)
    {
        int direction = isWhite ? -1 : 1;

        if (fromCol == toCol)
        {
            if (board[toRow, toCol] == Piece.None)
            {
                if (toRow == fromRow + direction)
                {
                    return true;
                }

                if (toRow == fromRow + 2 * direction &&
                    ((isWhite && fromRow == 6) || (!isWhite && fromRow == 1)) &&
                    board[fromRow + direction, fromCol] == Piece.None)
                {
                    return true;
                }
            }
        }
        else if (Math.Abs(fromCol - toCol) == 1 && toRow == fromRow + direction)
        {
            if (board[toRow, toCol] != Piece.None) return true;

            int toPos = toRow * 8 + toCol;

            if (toPos == enPassantSquare) return true;
        }

        Log(Utils.GetMessage(Messages.Board_InvalidMove, isWhite ? Messages.Board_Color_White : Messages.Board_Color_Black, Messages.Board_Pieces_Pawn));
        return false;
    }

    private bool IsValidKnightMove(int fromRow, int fromCol, int toRow, int toCol, bool isWhite)
    {
        int rowDiff = Math.Abs(fromRow - toRow);
        int colDiff = Math.Abs(fromCol - toCol);
        bool valid = (rowDiff == 2 && colDiff == 1) || (rowDiff == 1 && colDiff == 2);

        if (!valid)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove, isWhite ? Messages.Board_Color_White : Messages.Board_Color_Black, Messages.Board_Pieces_Knight));
        }

        return valid;
    }

    private bool IsValidBishopMove(int fromRow, int fromCol, int toRow, int toCol, bool isWhite, bool log = true)
    {
        bool valid = true;

        if (Math.Abs(fromRow - toRow) != Math.Abs(fromCol - toCol))
        {
            valid = false;
        }
        else
        {
            valid = IsPathClear(fromRow, fromCol, toRow, toCol);
        }

        if (!valid && log)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove, isWhite ? Messages.Board_Color_White : Messages.Board_Color_Black, Messages.Board_Pieces_Bishop));
        }

        return valid;
    }

    private bool IsValidRookMove(int fromRow, int fromCol, int toRow, int toCol, bool isWhite, bool log = true)
    {
        bool valid = true;

        if (fromRow != toRow && fromCol != toCol)
        {
            valid = false;
        }
        else
        {
            valid = IsPathClear(fromRow, fromCol, toRow, toCol);
        }

        if (!valid && log)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove, isWhite ? Messages.Board_Color_White : Messages.Board_Color_Black, Messages.Board_Pieces_Rook));
        }

        return valid;
    }

    private bool IsValidQueenMove(int fromRow, int fromCol, int toRow, int toCol, bool isWhite)
    {
        bool valid = IsValidBishopMove(fromRow, fromCol, toRow, toCol, isWhite, false) || IsValidRookMove(fromRow, fromCol, toRow, toCol, isWhite, false);

        if (!valid)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove, isWhite ? Messages.Board_Color_White : Messages.Board_Color_Black, Messages.Board_Pieces_Queen));
        }

        return valid;
    }

    private bool IsValidKingMove(int fromRow, int fromCol, int toRow, int toCol, bool isWhite)
    {
        int rowDiff = Math.Abs(fromRow - toRow);
        int colDiff = Math.Abs(fromCol - toCol);
        bool valid = true;

        if (rowDiff <= 1 && colDiff <= 1)
        {
            valid = true;
        }
        else if (rowDiff == 0 && colDiff == 2)
        {
            if (fromCol != 4)
            {
                valid = false;
            }
            else if (toCol == 6)
            {
                if (isWhite)
                {
                    if (!castlingRight[(int)Castling.WhiteKingSide]) valid = false;
                    if (board[7, 7] != Piece.WhiteRook) valid = false;
                }
                else
                {
                    if (!castlingRight[(int)Castling.BlackKingSide]) valid = false;
                    if (board[0, 7] != Piece.BlackRook) valid = false;
                }

                if (board[fromRow, 5] != Piece.None || board[fromRow, 6] != Piece.None) valid = false;

                if (IsPositionUnderAttack(fromRow, fromCol, !isWhite)) valid = false;
                if (IsPositionUnderAttack(fromRow, 5, !isWhite)) valid = false;
                if (IsPositionUnderAttack(fromRow, 6, !isWhite)) valid = false;
            }
            else if (toCol == 2)
            {
                if (isWhite)
                {
                    if (!castlingRight[(int)Castling.WhiteQueenSide]) valid = false;
                    if (board[7, 0] != Piece.WhiteRook) valid = false;
                }
                else
                {
                    if (!castlingRight[(int)Castling.BlackQueenSide]) valid = false;
                    if (board[0, 0] != Piece.BlackRook) valid = false;
                }

                if (board[fromRow, 1] != Piece.None || board[fromRow, 2] != Piece.None || board[fromRow, 3] != Piece.None) valid = false;

                if (IsPositionUnderAttack(fromRow, fromCol, !isWhite)) valid = false;
                if (IsPositionUnderAttack(fromRow, 3, !isWhite)) valid = false;
                if (IsPositionUnderAttack(fromRow, 2, !isWhite)) valid = false;
            }
        }

        if (!valid)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove, isWhite ? Messages.Board_Color_White : Messages.Board_Color_Black, Messages.Board_Pieces_King));
        }

        return valid;
    }

    private bool CanPawnAttack(int fromRow, int fromCol, int row, int col, bool pieceIsWhite)
    {
        int dir = pieceIsWhite ? -1 : 1;

        return fromRow + dir == row && Math.Abs(fromCol - col) == 1;
    }

    private bool CanKnightAttack(int fromRow, int fromCol, int row, int col)
    {
        int rowDiff = Math.Abs(fromRow - row);
        int colDiff = Math.Abs(fromCol - col);

        return (rowDiff == 2 && colDiff == 1) || (rowDiff == 1 && colDiff == 2);
    }

    private bool CanBishopAttack(int fromRow, int fromCol, int row, int col)
    {
        if (Math.Abs(fromRow - row) != Math.Abs(fromCol - col)) return false;

        return IsPathClear(fromRow, fromCol, row, col);
    }

    private bool CanRookAttack(int fromRow, int fromCol, int row, int col)
    {
        if (fromRow != row && fromCol != col) return false;

        return IsPathClear(fromRow, fromCol, row, col);
    }

    private bool CanKingAttack(int fromRow, int fromCol, int row, int col)
    {
        return Math.Abs(fromRow - row) <= 1 && Math.Abs(fromCol - col) <= 1;
    }

    private int FindPiece(Piece piece)
    {
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                if (board[i, j] == piece)
                {
                    return i * 8 + j;
                }
            }
        }

        return -1;
    }

    private bool IsPathClear(int fromRow, int fromCol, int toRow, int toCol)
    {
        int stepRow = Math.Sign(toRow - fromRow);
        int stepCol = Math.Sign(toCol - fromCol);

        if (stepRow == 0 && stepCol == 0) return false;

        int r = fromRow + stepRow;
        int c = fromCol + stepCol;

        while (r != toRow || c != toCol)
        {
            if (board[r, c] != Piece.None) return false;

            r += stepRow;
            c += stepCol;
        }

        return true;
    }

    private bool IsPositionUnderAttack(int row, int col, bool byWhite)
    {
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                Piece piece = board[i, j];

                if (piece == Piece.None) continue;

                bool pieceIsWhite = IsWhite(piece);

                if (pieceIsWhite != byWhite) continue;

                int fromRow = i;
                int fromCol = j;

                switch (piece)
                {
                    case Piece.WhitePawn:
                    case Piece.BlackPawn:
                    {
                        if (CanPawnAttack(fromRow, fromCol, row, col, pieceIsWhite)) return true;
                    }
                    break;

                    case Piece.WhiteKnight:
                    case Piece.BlackKnight:
                    {
                        if (CanKnightAttack(fromRow, fromCol, row, col)) return true;
                    }
                    break;

                    case Piece.WhiteBishop:
                    case Piece.BlackBishop:
                    {
                        if (CanBishopAttack(fromRow, fromCol, row, col)) return true;
                    }
                    break;

                    case Piece.WhiteRook:
                    case Piece.BlackRook:
                    {
                        if (CanRookAttack(fromRow, fromCol, row, col)) return true;
                    }
                    break;

                    case Piece.WhiteQueen:
                    case Piece.BlackQueen:
                    {
                        if (CanBishopAttack(fromRow, fromCol, row, col) || CanRookAttack(fromRow, fromCol, row, col)) return true;
                    }
                    break;

                    case Piece.WhiteKing:
                    case Piece.BlackKing:
                    {
                        if (CanKingAttack(fromRow, fromCol, row, col)) return true;
                    }
                    break;
                }
            }
        }

        return false;
    }

    private bool IsPromotionPiece(Piece p, bool isWhite)
    {
        bool result = p switch
        {
            Piece.WhiteQueen or Piece.WhiteRook or Piece.WhiteBishop or Piece.WhiteKnight => isWhite,
            Piece.BlackQueen or Piece.BlackRook or Piece.BlackBishop or Piece.BlackKnight => !isWhite,
            _ => false
        };

        if (!result)
        {
            Log(Utils.GetMessage(Messages.Board_InvalidMove_PromotionPiece, Messages.Board_Pieces_Pawn));
        }

        return result;
    }

    private bool TryParsePiece(char c, out Piece piece)
    {
        piece = c switch
        {
            'P' => Piece.WhitePawn,
            'p' => Piece.BlackPawn,
            'N' => Piece.WhiteKnight,
            'n' => Piece.BlackKnight,
            'B' => Piece.WhiteBishop,
            'b' => Piece.BlackBishop,
            'R' => Piece.WhiteRook,
            'r' => Piece.BlackRook,
            'Q' => Piece.WhiteQueen,
            'q' => Piece.BlackQueen,
            'K' => Piece.WhiteKing,
            'k' => Piece.BlackKing,
            _ => Piece.None
        };

        return piece != Piece.None;
    }

    private string ToAlgebraic(int position)
    {
        return $"{(char)('a' + position % 8)}{8 - position / 8}";
    }

    private int FromAlgebraic(string position)
    {
        if (string.IsNullOrEmpty(position) || position.Length != 2 || position[0] < 'a' || position[0] > 'h' || position[1] < '1' || position[1] > '8')
        {
            return -1;
        }

        return (position[0] - 'a') + (8 - (position[1] - '0')) * 8;
    }

    private void Log(string message, Exception? ex = null, [CallerMemberName] string memberName = "", [CallerLineNumber] int lineNumber = 0)
    {
        Utils.Log(_logger, message, ex, memberName, lineNumber);
    }

    private bool IsPseudoLegalMove(Piece piece, Piece targetPiece, int fromPos, int toPos)
    {
        int fromRow = fromPos / 8;
        int fromCol = fromPos % 8;
        int toRow = toPos / 8;
        int toCol = toPos % 8;

        if (SameColor(piece, targetPiece))
        {
            return false;
        }

        bool isWhite = IsWhite(piece);

        switch (piece)
        {
            case Piece.WhitePawn:
            case Piece.BlackPawn:
            {
                int direction = isWhite ? -1 : 1;

                if (fromCol == toCol)
                {
                    if (board[toRow, toCol] == Piece.None)
                    {
                        if (toRow == fromRow + direction) return true;

                        if (toRow == fromRow + 2 * direction &&
                            ((isWhite && fromRow == 6) || (!isWhite && fromRow == 1)) &&
                            board[fromRow + direction, fromCol] == Piece.None)
                        {
                            return true;
                        }
                    }
                }
                else if (Math.Abs(fromCol - toCol) == 1 && toRow == fromRow + direction)
                {
                    if (board[toRow, toCol] != Piece.None) return true;

                    int toP = toRow * 8 + toCol;
                    if (toP == enPassantSquare) return true;
                }

                return false;
            }

            case Piece.WhiteKnight:
            case Piece.BlackKnight:
            {
                int rowDiff = Math.Abs(fromRow - toRow);
                int colDiff = Math.Abs(fromCol - toCol);
                return (rowDiff == 2 && colDiff == 1) || (rowDiff == 1 && colDiff == 2);
            }

            case Piece.WhiteBishop:
            case Piece.BlackBishop:
            {
                return IsValidBishopMove(fromRow, fromCol, toRow, toCol, isWhite, false);
            }

            case Piece.WhiteRook:
            case Piece.BlackRook:
            {
                return IsValidRookMove(fromRow, fromCol, toRow, toCol, isWhite, false);
            }

            case Piece.WhiteQueen:
            case Piece.BlackQueen:
            {
                return IsValidBishopMove(fromRow, fromCol, toRow, toCol, isWhite, false)
                    || IsValidRookMove(fromRow, fromCol, toRow, toCol, isWhite, false);
            }

            case Piece.WhiteKing:
            case Piece.BlackKing:
            {
                int rowDiff = Math.Abs(fromRow - toRow);
                int colDiff = Math.Abs(fromCol - toCol);

                if (rowDiff <= 1 && colDiff <= 1) return true;

                if (rowDiff == 0 && colDiff == 2)
                {
                    if (fromCol != 4) return false;

                    if (toCol == 6)
                    {
                        if (isWhite)
                        {
                            if (!castlingRight[(int)Castling.WhiteKingSide]) return false;
                            if (board[7, 7] != Piece.WhiteRook) return false;
                        }
                        else
                        {
                            if (!castlingRight[(int)Castling.BlackKingSide]) return false;
                            if (board[0, 7] != Piece.BlackRook) return false;
                        }

                        if (board[fromRow, 5] != Piece.None || board[fromRow, 6] != Piece.None) return false;

                        if (IsPositionUnderAttack(fromRow, fromCol, !isWhite)) return false;
                        if (IsPositionUnderAttack(fromRow, 5, !isWhite)) return false;
                        if (IsPositionUnderAttack(fromRow, 6, !isWhite)) return false;

                        return true;
                    }
                    else if (toCol == 2)
                    {
                        if (isWhite)
                        {
                            if (!castlingRight[(int)Castling.WhiteQueenSide]) return false;
                            if (board[7, 0] != Piece.WhiteRook) return false;
                        }
                        else
                        {
                            if (!castlingRight[(int)Castling.BlackQueenSide]) return false;
                            if (board[0, 0] != Piece.BlackRook) return false;
                        }

                        if (board[fromRow, 1] != Piece.None || board[fromRow, 2] != Piece.None || board[fromRow, 3] != Piece.None) return false;

                        if (IsPositionUnderAttack(fromRow, fromCol, !isWhite)) return false;
                        if (IsPositionUnderAttack(fromRow, 3, !isWhite)) return false;
                        if (IsPositionUnderAttack(fromRow, 2, !isWhite)) return false;

                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    private bool HasLegalMoves(bool forWhite)
    {
        for (int fromPos = 0; fromPos < 64; fromPos++)
        {
            int fromRow = fromPos / 8;
            int fromCol = fromPos % 8;
            Piece piece = board[fromRow, fromCol];
            if (piece == Piece.None) continue;

            bool pieceIsWhite = IsWhite(piece);
            if (pieceIsWhite != forWhite) continue;

            for (int toPos = 0; toPos < 64; toPos++)
            {
                if (toPos == fromPos) continue;

                int toRow = toPos / 8;
                int toCol = toPos % 8;
                Piece target = board[toRow, toCol];

                if (!IsPseudoLegalMove(piece, target, fromPos, toPos)) continue;

                var boardBackup = (Piece[,])board.Clone();
                var castlingBackup = (bool[])castlingRight.Clone();
                int enPassantBackup = enPassantSquare;

                bool pawnMove = (piece == Piece.WhitePawn || piece == Piece.BlackPawn);
                bool capture = target != Piece.None;

                if (pawnMove && toPos == enPassantSquare && target == Piece.None)
                {
                    int dir = pieceIsWhite ? -1 : 1;
                    int capturedRow = toRow - dir;
                    board[capturedRow, toCol] = Piece.None;
                    capture = true;
                }

                board[toRow, toCol] = piece;
                board[fromRow, fromCol] = Piece.None;

                if (piece == Piece.WhiteKing || piece == Piece.BlackKing)
                {
                    if (Math.Abs(toCol - fromCol) == 2)
                    {
                        if (toCol > fromCol)
                        {
                            MoveRookForCastling(fromRow, 7, toCol - 1);
                        }
                        else
                        {
                            MoveRookForCastling(fromRow, 0, toCol + 1);
                        }
                    }
                }

                Piece promoted = Piece.None;
                if (piece == Piece.WhitePawn && toRow == 0)
                {
                    promoted = Piece.WhiteQueen;
                    board[toRow, toCol] = promoted;
                }
                else if (piece == Piece.BlackPawn && toRow == 7)
                {
                    promoted = Piece.BlackQueen;
                    board[toRow, toCol] = promoted;
                }

                Piece kingPiece = forWhite ? Piece.WhiteKing : Piece.BlackKing;
                int kingPos = FindPiece(kingPiece);

                bool inCheck = kingPos == -1 || IsPositionUnderAttack(kingPos / 8, kingPos % 8, !forWhite);

                board = (Piece[,])boardBackup.Clone();
                castlingRight = (bool[])castlingBackup.Clone();
                enPassantSquare = enPassantBackup;

                if (!inCheck) return true;
            }
        }

        return false;
    }

    private bool SameColor(Piece a, Piece b)
    {
        return a != Piece.None && b != Piece.None && ((IsWhite(a) && IsWhite(b)) || (IsBlack(a) && IsBlack(b)));
    }

    private void MoveRookForCastling(int row, int rookFromCol, int rookToCol)
    {
        board[row, rookToCol] = board[row, rookFromCol];
        board[row, rookFromCol] = Piece.None;
    }

    private void RestoreBackup(Piece[,] boardBackup, bool[] castlingBackup, int enPassantBackup, int halfMoveBackup, int fullMoveBackup, bool whiteTurnBackup)
    {
        board = (Piece[,])boardBackup.Clone();
        castlingRight = (bool[])castlingBackup.Clone();
        enPassantSquare = enPassantBackup;
        halfMove = halfMoveBackup;
        fullMove = fullMoveBackup;
        whiteTurn = whiteTurnBackup;
    }

    private Piece ChoosePromotion(string from, string to, bool isWhite)
    {
        if (promotionCallback != null)
        {
            Piece choice = promotionCallback(from, to);
            return IsPromotionPiece(choice, isWhite) ? choice : (isWhite ? Piece.WhiteQueen : Piece.BlackQueen);
        }

        return isWhite ? Piece.WhiteQueen : Piece.BlackQueen;
    }
}