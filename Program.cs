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
                    .AddSingleton<ConsoleRenderer>();
            });

        IHost host = builder.Build();

        ILogger<Program>? logger = host.Services.GetRequiredService<ILogger<Program>>();

        bool testsOk = RunTests(logger);

        Console.WriteLine();
        Console.WriteLine("Tests finished. Press any key to continue...");
        Console.ReadKey(intercept: true);

        if (testsOk)
        {
            ConsoleRenderer renderer = host.Services.GetRequiredService<ConsoleRenderer>();

            await renderer.Run();
        }
    }

    private static bool RunTests(ILogger<Program>? logger)
    {
        int failed = 0;

        Log(logger, $"Start engine test...{Environment.NewLine}");

        RunTest("Basic moves...",
            new Board(),
            [
                ("e2", "e4", true, "White pawn two-step — no en-passant square"),
                ("b8", "c6", true, "Development of black knight"),
                ("a7", "a6", false, "Attempt to move black pawn on white's turn"),
                ("g1", "f3", true, "Development of white knight"),
                ("a7", "a6", true, "Now black pawn can move"),
                ("f1", "a6", true, "White bishop takes black pawn"),
                ("b7", "a6", true, "Black pawnt takes white bishop"),
                ("a1", "a3", false, "Rook blocked by own pawn at a2")
            ],
            logger,
            ref failed);

        RunTest("Promotion test (white)...",
            new Board("8/4P2k/8/8/8/8/8/4K3 w - - 0 1"),
            [
                ("e7", "e8", true, "White pawn promotes - expect queen by default")
            ],
            logger,
            ref failed);

        RunTest("Promotion test (black)...",
            new Board("4k3/8/8/8/8/8/4p3/4K3 b - - 0 1"),
            [
                ("e2", "e1", false, "Black pawn promotes on e1 — blocked by white king")
            ],
            logger,
            ref failed);

        RunTest("Kingside castling test...",
            new Board("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1"),
            [
                ("e1", "g1", true, "White kingside castle"),
                ("e8", "g8", false, "Black can't castle queenside becasue of crossing chess"),
                ("e8", "c8", true, "Black kingside castle")
            ],
            logger,
            ref failed);

        RunTest("Queenside castling test...",
            new Board("r3k2r/8/8/8/8/8/8/R3K2R b KQkq - 0 1"),
            [
                ("e8", "c8", true, "Black queenside castle"),
                ("e1", "c1", false, "White can't castle kingside becasue of crossing chess."),
                ("e1", "g1", true, "White queenside castle")
            ],
            logger,
            ref failed);

        RunTest("Illegal castle test...",
            new Board("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1"),
            [
                ("a1","a2", true, "Move white rook"),
                ("h8","h7", true, "Move black rook"),
                ("e1","c1", false, "Attempt castling after rook moved")
            ],
            logger,
            ref failed);

        RunTest("En-passant sequence test...",
            new Board(),
            [
                ("e2","e4", true, "White pawn two-step — no en-passant square"),
                ("a7","a6", true, "Black pawn single step"),
                ("e4","e5", true, "White pawn single step"),
                ("d7","d5", true, "Black pawn two-step — eligible for en-passant"),
                ("e5","d6", true, "White captures en-passant pawn")
            ],
            logger,
            ref failed);

        RunTest("Pawn capture test...",
            new Board("4k3/8/8/3p4/2P5/8/8/4K3 w - - 0 1"),
            [
                ("c4", "d5", true, "White pawn captures black pawn")
            ],
            logger,
            ref failed);

        RunTest("Illegal notation test...",
            new Board(),
            [
                ("e2", "e", false, "Invalid notation - missing rank"),
                ("e2", "4", false, "Invalid notation - missing file"),
                ("e2", "4e", false, "Invalid notation - wrong order")
            ],
            logger,
            ref failed);

        RunTest("Pin test...",
            new Board("4r3/8/2k5/8/8/8/4N3/4K3 w - - 0 1"),
            [
                ("e2", "d4", false, "Knight pinned to king - can't move")
            ],
            logger,
            ref failed);

        if (failed > 0)
        {
            Log(logger, $"{Environment.NewLine}Engine test failed! Exiting...");

            return false;
        }
        else
        {
            Log(logger, $"{Environment.NewLine}Engine test done.");

            return true;
        }
    }

    private static void RunTest(string description, Board board, (string from, string to, bool expect, string note)[] moves, ILogger<Program>? logger, ref int failed)
    {
        int step = 1;

        Log(logger, $"{Environment.NewLine}Running: {description}");

        foreach (var move in moves)
        {
            bool ok;
            Exception? ex = null;

            Log(logger, $"[MOVE {step}] {move.from}-{move.to} | {move.note}");

            try
            {
                ok = board.Move(move.from, move.to);
            }
            catch (Exception e)
            {
                ok = false;
                ex = e;
            }

            if (ok == move.expect)
            {
                Log(logger, $"\t[PASS] expected={move.expect}/actual={ok}");
            }
            else
            {
                failed++;

                Log(logger, $"\t[FAIL] expected={move.expect}/actual={ok}");
            }

            if (ex != null)
            {
                Log(logger, $"\t[EXCEPTION] {ex.Message}");
            }

            step++;
        }
    }

    private static void Log(ILogger<Program>? logger, string message)
    {
#if DEBUG
        Console.WriteLine(message);
#else
        if (logger != null)
        {
            logger.LogError("{Message}", message);
        }
        else
        {
            Console.WriteLine(message);
        }
#endif
    }
}