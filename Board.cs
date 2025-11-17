using System.ComponentModel;
using System.Net.Http.Headers;

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
    public bool EnPassant { get; set; }
    public bool KingCastling { get; set; }
    public bool QueenCastling { get; set; }
    public bool Check { get; set; }
    public bool Checkmate { get; set; }
    public bool Promotion { get; set; }
    public Piece PromotedTo { get; set; }
}

internal class Board
{
    protected string noValue = "-";

    protected Piece[,] board = new Piece[8, 8];

    protected bool[] castlingRight = { true, true, true, true };
    protected int halfMove = 0;
    protected int fullMove = 1;
    protected bool whiteTurn = true;
    protected int enPassantSquare = -1;

    protected string startingFen { get; set; } = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public Board()
    {
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

        fen += " " + (enPassantSquare == -1 ? noValue : ToAlgebric(enPassantSquare));

        fen += " " + halfMove;
        fen += " " + fullMove;

        return fen;
    }

    public void FromFen(string fen)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(fen, nameof(fen));

        string[] parts = fen.Split(' ');

        if (parts.Length != 6) throw new ArgumentException("Invalid Fen string");

        string[] rows = parts[0].Split('/');

        if (rows.Length != 8) throw new ArgumentException("Invalid Fen string");

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
                        board[i, col] = Piece.None;
                        col++;
                    }
                }
                else
                {
                    board[i, col] = (Piece)c;
                    col++;
                }
            }
        }

        whiteTurn = parts[1] == "w";

        castlingRight[(int)Castling.WhiteKingSide] = parts[2].Contains('K');
        castlingRight[(int)Castling.WhiteQueenSide] = parts[2].Contains('Q');
        castlingRight[(int)Castling.BlackKingSide] = parts[2].Contains('k');
        castlingRight[(int)Castling.BlackQueenSide] = parts[2].Contains('q');

        enPassantSquare = parts[3] == "-" ? -1 : (parts[3][0] - 'a') + (8 - (parts[3][1] - '0')) * 8;

        halfMove = int.Parse(parts[4]);
        fullMove = int.Parse(parts[5]);

        startingFen = fen;
    }

    public bool Move(string from, string to)
    {
        int fromPos = FromAlgebric(from);
        int toPos = FromAlgebric(to);

        Piece piece = board[fromPos / 8, fromPos % 8];
        Piece targetPiece = board[toPos / 8, toPos % 8];

        bool isWhite = char.IsUpper((char)piece);

        if(isWhite && !whiteTurn || !isWhite && whiteTurn)
        {
            return false;
        }

        if (piece == Piece.None)
        {
            return false;
        }

        if (!IsValidMove(piece, targetPiece, fromPos, toPos))
        {
            return false;
        }

        board[toPos / 8, toPos % 8] = piece;
        board[fromPos / 8, fromPos % 8] = Piece.None;

        int kingPos = FindPiece(isWhite ? Piece.WhiteKing : Piece.BlackKing);

        if(IsPositionUnderAttack(isWhite ? Piece.WhiteKing : Piece.BlackKing, kingPos / 8, kingPos % 8, isWhite))
        {
            board[toPos / 8, toPos % 8] = targetPiece;
            board[fromPos / 8, fromPos % 8] = piece;

            return false;
        }

        whiteTurn = !whiteTurn;

        return true;
    }

    private bool IsValidMove(Piece piece, Piece targetPiece, int fromPos, int toPos)
    {
        int fromRow = fromPos / 8;
        int fromCol = fromPos % 8;
        int toRow = toPos / 8;
        int toCol = toPos % 8;

        if ((char.IsUpper((char)piece) && char.IsUpper((char)targetPiece)) ||
            (char.IsLower((char)piece) && char.IsLower((char)targetPiece)))
        {
            return false;
        }

        switch (piece)
        {
            case Piece.WhitePawn:
            {
                return IsValidPawnMove(fromRow, fromCol, toRow, toCol, true);
            }

            case Piece.BlackPawn:
            {
                return IsValidPawnMove(fromRow, fromCol, toRow, toCol, false);
            }

            case Piece.WhiteKnight:
            case Piece.BlackKnight:
            {
                return IsValidKnightMove(fromRow, fromCol, toRow, toCol);
            }

            case Piece.WhiteBishop:
            case Piece.BlackBishop:
            {
                return IsValidBishopMove(fromRow, fromCol, toRow, toCol);
            }

            case Piece.WhiteRook:
            case Piece.BlackRook:
            {
                return IsValidRookMove(fromRow, fromCol, toRow, toCol);
            }

            case Piece.WhiteQueen:
            case Piece.BlackQueen:
            {
                return IsValidQueenMove(fromRow, fromCol, toRow, toCol);
            }

            case Piece.WhiteKing:
            case Piece.BlackKing:
            {
                return IsValidKingMove(fromRow, fromCol, toRow, toCol);
            }
        }

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
                    (isWhite && fromRow == 6 || !isWhite && fromRow == 1) &&
                    board[fromRow + direction, fromCol] == Piece.None)
                {
                    return true;
                }
            }
        }
        else if (Math.Abs(fromCol - toCol) == 1 && toRow == fromRow + direction && board[toRow, toCol] != Piece.None)
        {
            return true;
        }

        return false;
    }

    private bool IsValidKnightMove(int fromRow, int fromCol, int toRow, int toCol)
    {
        int rowDiff = Math.Abs(fromRow - toRow);
        int colDiff = Math.Abs(fromCol - toCol);

        return (rowDiff == 2 && colDiff == 1) || (rowDiff == 1 && colDiff == 2);
    }

    private bool IsValidBishopMove(int fromRow, int fromCol, int toRow, int toCol)
    {
        if (Math.Abs(fromRow - toRow) != Math.Abs(fromCol - toCol))
        {
            return false;
        }

        int rowDirection = (toRow - fromRow) / Math.Abs(toRow - fromRow);
        int colDirection = (toCol - fromCol) / Math.Abs(toCol - fromCol);
        int row = fromRow + rowDirection;
        int col = fromCol + colDirection;

        while (row != toRow && col != toCol)
        {
            if (board[row, col] != Piece.None)
            {
                return false;
            }

            row += rowDirection;
            col += colDirection;
        }

        return true;
    }

    private bool IsValidRookMove(int fromRow, int fromCol, int toRow, int toCol)
    {
        if (fromRow != toRow && fromCol != toCol)
        {
            return false;
        }

        int rowDirection = fromRow == toRow ? 0 : (toRow - fromRow) / Math.Abs(toRow - fromRow);
        int colDirection = fromCol == toCol ? 0 : (toCol - fromCol) / Math.Abs(toCol - fromCol);
        int row = fromRow + rowDirection;
        int col = fromCol + colDirection;

        while (row != toRow || col != toCol)
        {
            if (board[row, col] != Piece.None)
            {
                return false;
            }

            row += rowDirection;
            col += colDirection;
        }

        return true;
    }

    private bool IsValidQueenMove(int fromRow, int fromCol, int toRow, int toCol)
    {
        return IsValidBishopMove(fromRow, fromCol, toRow, toCol) || IsValidRookMove(fromRow, fromCol, toRow, toCol);
    }

    private bool IsValidKingMove(int fromRow, int fromCol, int toRow, int toCol)
    {
        int rowDiff = Math.Abs(fromRow - toRow);
        int colDiff = Math.Abs(fromCol - toCol);

        return rowDiff <= 1 && colDiff <= 1;
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

    private bool IsPositionUnderAttack(Piece targetPiece, int row, int col, bool byWhite)
    {
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                Piece piece = board[i, j];

                if (piece != Piece.None && (char.IsUpper((char)piece) == byWhite))
                {
                    int fromPos = i * 8 + j;
                    int toPos = row * 8 + col;

                    if (IsValidMove(piece, targetPiece, fromPos, toPos))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    protected string ToAlgebric(int position)
    {
        if (position < 0 || position > 63) throw new ArgumentOutOfRangeException("Invalid board position.", nameof(position));

        return $"{(char)('a' + position % 8)}{8 - position / 8}";
    }

    protected int FromAlgebric(string position)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(position, nameof(position));

        return (position[0] - 'a') + (8 - (position[1] - '0')) * 8;
    }
}
