using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Promote;

internal class Program
{
    static async Task Main(string[] args)
    {
        IHostBuilder builder = Host.CreateDefaultBuilder(args)
            .ConfigureLogging((context, logging) =>
            {
                logging.AddEventSourceLogger();
                logging.AddDebug();
                logging.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";
                });
            })
            .ConfigureServices((context, services) =>
            {
                Settings settings = context.Configuration.Get<Settings>() ?? new Settings();

                services.AddSingleton(settings)
                    .Configure<Settings>(context.Configuration)
                    .AddSingleton<Engine>()
                    .AddSingleton<Board>()
                    .AddSingleton<IRenderer, ConsoleRenderer>();
            });

        IHost host = builder.Build();

        await Run();

        await host.RunAsync();
    }

    private static async Task Run()
    {
        Console.WriteLine("Board move tests starting...\n");

        // Base starting position
        var start = new Board();

        // Basic tests (existing)
        await RunTest("White pawn two-step (sets en-passant)", new Board(), "e2", "e4", true);
        await RunTest("Illegal: move opponent piece (black pawn on a7 during white turn)", start, "a7", "a6", false);
        await RunTest("Knight basic move g1->f3", start, "g1", "f3", true);
        await RunTest("Blocked bishop f1->a6 (initial position)", start, "f1", "a6", false);
        await RunTest("Blocked rook a1->a3 (initial position)", start, "a1", "a3", false);

        // Promotion tests
        {
            var fenPromoteWhite = "8/4P2k/8/8/8/8/8/4K3 w - - 0 1";
            var b = new Board(); b.FromFen(fenPromoteWhite);
            await RunTest("Pawn promotion e7->e8 (white promotes to queen)", b, "e7", "e8", true);
        }
        {
            // Corrected FEN: black pawn on e2 (rank 2) and black to move
            var fenPromoteBlack = "4k3/8/8/8/8/8/4p3/4K3 b - - 0 1";
            var b = new Board(); b.FromFen(fenPromoteBlack);
            await RunTest("Pawn promotion e2->e1 (black promotes to queen)", b, "e2", "e1", true);
        }

        // Castling tests
        {
            var fenCastle = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
            var b = new Board(); b.FromFen(fenCastle);
            await RunTest("White kingside castling e1->g1", b, "e1", "g1", true);
            b.FromFen(fenCastle);
            await RunTest("White queenside castling e1->c1", b, "e1", "c1", true);
            // Black side - use same piece placement but black to move
            b.FromFen(fenCastle.Replace(" w ", " b "));
            await RunTest("Black kingside castling e8->g8", b, "e8", "g8", true);
            b.FromFen(fenCastle.Replace(" w ", " b "));
            await RunTest("Black queenside castling e8->c8", b, "e8", "c8", true);
        }

        // Illegal castling: rook moved or path attacked
        {
            var fen = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
            var b = new Board(); b.FromFen(fen);
            // move rook first (a1->a2), then try castle
            await RunSequence("Illegal castle after rook moved", b, new[]{
                ("a1","a2", true),
                ("e1","g1", false)
            });
        }

        // En-passant full sequence (from starting position)
        {
            var b = new Board();
            await RunSequence("En-passant sequence e2-e4, a7-a6, e4-e5, d7-d5, e5xd6 ep", b, new[]{
                ("e2","e4", true),
                ("a7","a6", true),
                ("e4","e5", true),
                ("d7","d5", true),
                ("e5","d6", true) // en-passant capture of pawn from d5
            });
        }

        // Pawn capture and non-capture tests
        {
            // simple capture: white pawn on c4 can capture black pawn on d5
            var fen = "8/8/8/3p4/2P5/8/8/4K3 w - - 0 1"; // white pawn c4 (c4), black pawn d5
            var b = new Board(); b.FromFen(fen);
            await RunTest("Pawn capture c4xd5", b, "c4", "d5", true);
        }

        // Knight crazy moves and invalid notation
        await RunTest("Illegal notation (too short) -> should throw/catch", start, "e2", "e", false);
        await RunTest("Knight jump b1->c3", start, "b1", "c3", true);

        // Sliding pieces: rook/ bishop / queen path blocking and capturing
        {
            // Bishop on c3 is black so set side to move to 'b'
            var fen = "8/8/8/8/8/2b5/8/4K3 b - - 0 1"; // bishop on c3 able to move
            var b = new Board(); b.FromFen(fen);
            await RunTest("Bishop c3->g7 (diagonal)", b, "c3", "g7", true);
        }
        {
            // Rook on b2 is black so set side to move to 'b'
            var fen = "8/8/8/8/8/8/1r6/4K3 b - - 0 1"; // rook on b2
            var b = new Board(); b.FromFen(fen);
            await RunTest("Rook b2->b4 (vertical)", b, "b2", "b4", true);
            await RunTest("Rook b2->e2 (horizontal)", b, "b2", "e2", true);
        }

        // Pin detection (existing pinned knight test)
        {
            var fenPin = "4r3/8/8/8/8/8/4N3/4K3 w - - 0 1";
            var b = new Board(); b.FromFen(fenPin);
            await RunTest("Illegal: moving pinned knight e2->d4 (would expose king to rook)", b, "e2", "d4", false);
        }

        // Several "wild" randomized-ish small sequences to try varied rules
        {
            var b = new Board();
            await RunSequence("Wild sequence: develop pieces and test collisions",
                b,
                new[]{
                    ("g1","f3", true),   // N
                    ("g8","f6", true),   // n
                    ("f1","c4", true),   // B
                    ("f8","c5", true),   // b
                    ("d2","d4", true),   // pawn two
                    ("e7","e5", true),   // pawn two
                    ("d4","e5", true),   // pawn captures
                    ("f6","e4", true),   // knight jumps into center
                    ("e1","e2", false),  // illegal: king into own pawn (blocked) or move that might be invalid depending on board
                });
        }

        // Final summary
        Console.WriteLine($"\nTests finished. Passed: {passed}, Failed: {failed}");
    }

    private static int passed = 0;
    private static int failed = 0;

    private static async Task RunTest(string description, Board board, string from, string to, bool expect)
    {
        // Work on a fresh copy of the board passed in by cloning from its FEN to avoid test interference
        var b = new Board();
        b.FromFen(board.ToFen());

        bool ok;
        try
        {
            ok = b.Move(from, to);
        }
        catch (Exception ex)
        {
            ok = false;
            Console.WriteLine($"  [EXCEPTION] {ex.GetType().Name}: {ex.Message}");
        }

        var fen = b.ToFen();

        if (ok == expect)
        {
            passed++;
            Console.WriteLine($"[PASS] {description}: Move {from}->{to} -> expected={expect} actual={ok}");
        }
        else
        {
            failed++;
            Console.WriteLine($"[FAIL] {description}: Move {from}->{to} -> expected={expect} actual={ok}");
        }

        Console.WriteLine($"       Resulting FEN: {fen}");
    }

    /// <summary>
    /// Run a sequence of moves on a single board instance (moves alternate colors).
    /// Each tuple: (from, to, expectedResult).
    /// </summary>
    private static async Task RunSequence(string description, Board initialBoard, (string from, string to, bool expect)[] moves)
    {
        Console.WriteLine($"\n[SEQ] {description}");
        // clone initial board
        var b = new Board();
        b.FromFen(initialBoard.ToFen());

        int step = 1;
        foreach (var mv in moves)
        {
            bool ok;
            try
            {
                ok = b.Move(mv.from, mv.to);
            }
            catch (Exception ex)
            {
                ok = false;
                Console.WriteLine($"  [EXCEPTION] step {step} {mv.from}->{mv.to}: {ex.GetType().Name}: {ex.Message}");
            }

            if (ok == mv.expect)
            {
                passed++;
                Console.WriteLine($"  [PASS] step {step}: {mv.from}->{mv.to} -> expected={mv.expect} actual={ok}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] step {step}: {mv.from}->{mv.to} -> expected={mv.expect} actual={ok}");
            }

            Console.WriteLine($"         FEN: {b.ToFen()}");

            step++;
        }

        Console.WriteLine($"[SEQ END] {description} final FEN: {b.ToFen()}\n");
    }
}