using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using static Promote.Utils;

namespace Promote;

internal record BestMoveResult(string Move, string? Ponder, int? ScoreCp, int? ScoreMate);

internal class Engine : IDisposable
{
    public string EngineName { get; private set; } = string.Empty;
    public string EngineAuthor { get; private set; } = string.Empty;
    public List<UCIOption> EngineOptions { get; private set; } = new List<UCIOption>();

    private readonly EngineSettings _engineSettings;
    private readonly ILogger<Engine> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private ProcessStartInfo? _processInfo;
    private Process? _process;

    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();

    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly object _waitLock = new object();
    private TaskCompletionSource<string?>? _waitTcs;
    private string? _waitFlag;

    public Engine(IOptions<Settings> options, ILogger<Engine> logger, ILoggerFactory loggerFactory)
    {
        _engineSettings = options?.Value.Engine ?? new EngineSettings();

        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public void Dispose()
    {
        lock (_waitLock)
        {
            _waitTcs?.TrySetResult(null);
            _waitTcs = null;
            _waitFlag = null;
        }

        _sendLock.Dispose();

        StopProcess();
    }

    public async Task<bool> Start(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_engineSettings.Path) || !File.Exists(_engineSettings.Path))
        {
            Log(_logger, Messages.Engine_NoExecutable);
            return false;
        }

        bool started = false;

        if (StartProcess())
        {
            try
            {
                started = await UCI(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!started)
                {
                    StopProcess();
                }
                else
                {
                    Log(_logger, GetMessage(Messages.Engine_UCIStarted, EngineName, EngineAuthor));

                    await NewGame();
                }
            }
        }

        return started;
    }

    public async Task Stop()
    {
        if (_process is null) return;

        await QuitEngine().ConfigureAwait(false);

        Log(_logger, GetMessage(Messages.Engine_UCIStopped, EngineName, EngineAuthor));

        StopProcess();
    }

    public async Task NewGame()
    {
        await Send("ucinewgame", null).ConfigureAwait(false);

        await IsReady().ConfigureAwait(false);
    }

    public Task<bool> IsReady()
    {
        return Send("isready", "readyok");
    }

    public Task<bool> SetDebug(bool on)
    {
        return Send("debug " + (on ? "on" : "off"), null);
    }

    public async Task SetOption<T>(string name, T value)
    {
        await IsReady().ConfigureAwait(false);

        UCIOption? uciOption = EngineOptions.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));

        if (uciOption is null)
        {
            await Send($"setoption name {name} value {value?.ToString()}", null).ConfigureAwait(false);

            return;
        }

        UCIType<T>? uciType = uciOption.UCIType as UCIType<T>;

        if (uciType is UCIUnknown)
        {
            return;
        }
        else if (uciType != null)
        {
            uciType.Value = value;
        }
        else
        {
            string actualType = uciOption.UCIType?.GetType().Name ?? "null";
            Log(_logger, GetMessage(Messages.Engine_OptionTypeMissmatch, name, typeof(T).Name, actualType));
            return;
        }

        switch (uciOption.Type)
        {
            case OptionType.Button:
            {
                await Send($"setoption name {name}", null).ConfigureAwait(false);
            }
            break;

            case OptionType.Check:
            {
                await Send($"setoption name {name} value {((value as bool?) ?? false).ToString().ToLower()}", null).ConfigureAwait(false);

            }
            break;

            default:
            {
                await Send($"setoption name {name} value {value?.ToString()}", null).ConfigureAwait(false);
            }
            break;
        }
    }

    public async Task<bool> Send(string command)
    {
        if (_process is null) return false;

        return await Send(command, null);
    }

    public Task<bool> PositionFromFen(string fen)
    {
        if (string.IsNullOrEmpty(fen)) return Task.FromResult(false);

        return Send($"position fen {fen}", null);
    }

    public Task<bool> PositionFromMoves(IEnumerable<string>? moves)
    {
        if (_process is null) return Task.FromResult(false);

        if (moves is null)
        {
            return Send("position startpos", null);
        }

        string[] movesArr = moves.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray();

        if (movesArr.Length == 0)
        {
            return Send("position startpos", null);
        }

        string moveList = string.Join(' ', movesArr);

        return Send($"position startpos moves {moveList}", null);
    }

    public async Task<BestMoveResult?> GetBestMove(CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return null;

        bool sent = await Send($"go movetime {_engineSettings.Timeout}", "bestmove", cancellationToken).ConfigureAwait(false);

        if (!sent) return null;

        List<string> lines = ReadReceivedLines();

        string? bestLine = lines.FirstOrDefault(l => l.StartsWith("bestmove ", StringComparison.OrdinalIgnoreCase));

        if (bestLine is null) return null;

        string? move = null;
        string? ponder = null;
        int? scoreCp = null;
        int? scoreMate = null;

        string[] bestParts = bestLine.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        if (bestParts.Length >= 2)
        {
            move = bestParts[1];

            int pindex = Array.FindIndex(bestParts, p => string.Equals(p, "ponder", StringComparison.OrdinalIgnoreCase));
            if (pindex >= 0 && pindex + 1 < bestParts.Length)
            {
                ponder = bestParts[pindex + 1];
            }
        }

        foreach (string line in lines.Where(l => l.StartsWith("info ", StringComparison.OrdinalIgnoreCase)))
        {
            string[] parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], "score", StringComparison.OrdinalIgnoreCase) && i + 2 < parts.Length)
                {
                    string kind = parts[i + 1];
                    string value = parts[i + 2];

                    if (string.Equals(kind, "cp", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out int cp)) scoreCp = cp;
                        scoreMate = null;
                    }
                    else if (string.Equals(kind, "mate", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out int mate)) scoreMate = mate;
                        scoreCp = null;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(move)) return null;

        return new BestMoveResult(move, ponder, scoreCp, scoreMate);
    }

    private bool StartProcess()
    {
        _processInfo = new ProcessStartInfo()
        {
            FileName = _engineSettings.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _process = new Process() { StartInfo = _processInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += new DataReceivedEventHandler(OutputDataReceived);
        _process.ErrorDataReceived += new DataReceivedEventHandler(OutputDataReceived);
        _process.Exited += new EventHandler(ProcessExited);

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            Log(_logger, GetMessage(Messages.Engine_ProcessStartFailed), ex);

            return false;
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        return true;
    }

    private void StopProcess()
    {
        if (_process is null) return;

        _process.OutputDataReceived -= OutputDataReceived;
        _process.ErrorDataReceived -= OutputDataReceived;
        _process.Exited -= ProcessExited;

        _process.CancelOutputRead();
        _process.CancelErrorRead();

        try
        {
            if (!_process.HasExited)
            {
                if (_process.CloseMainWindow())
                {
                    _process.WaitForExit(1000);
                }
                else
                {
                    _process.StandardInput.Close();
                    _process.WaitForExit(1000);
                }

                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log(_logger, GetMessage(Messages.Engine_UnexpectedTryKill), ex);
        }
        finally
        {
            try
            {
                _process.Dispose();
            }
            catch (Exception ex)
            {
                Log(_logger, GetMessage(Messages.Engine_DisposeFail), ex);
            }

            _process = null;
        }
    }

    private void OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        string? raw = e?.Data;

        if (string.IsNullOrEmpty(raw)) return;

        string line = raw.Trim();

        _receiveQueue.Enqueue(line);

        lock (_waitLock)
        {
            if (!string.IsNullOrEmpty(_waitFlag) &&
                (string.Equals(line, _waitFlag, StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith(_waitFlag, StringComparison.OrdinalIgnoreCase)))
            {
                _waitTcs?.TrySetResult(line);
            }
        }
    }

    private void ProcessExited(object? sender, EventArgs e)
    {
        lock (_waitLock)
        {
            _waitTcs?.TrySetResult(null);
        }
    }

    private async Task<bool> UCI(CancellationToken cancellationToken = default)
    {
        bool ok = await Send("uci", "uciok", cancellationToken).ConfigureAwait(false);
        ILogger<UCIOption> uciLogger = _loggerFactory.CreateLogger<UCIOption>();

        if (!ok) return false;

        List<string> lines = ReadReceivedLines();

        List<string> idLines = lines.Where(l => l.StartsWith("id ", StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> optionLines = lines.Where(l => l.StartsWith("option ", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (string id in idLines)
        {
            string payload = id.Length > 3 ? id.Substring(3).Trim() : string.Empty;

            if (payload.StartsWith("name ", StringComparison.OrdinalIgnoreCase))
            {
                EngineName = payload.Substring(5).Trim();
            }
            else if (payload.StartsWith("author ", StringComparison.OrdinalIgnoreCase))
            {
                EngineAuthor = payload.Substring(7).Trim();
            }
        }

        EngineOptions.Clear();

        foreach (string opt in optionLines)
        {
            int index = opt.IndexOf(" type ", StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                string namePart = opt.Length > 12 ? opt.Substring(12).Trim() : string.Empty;
                EngineOptions.Add(new UCIOption(namePart, string.Empty, uciLogger, _loggerFactory));

                continue;
            }

            string namePartFixed = opt.Substring(12, index - 12).Trim();
            string typePart = opt.Substring(index + 6).Trim();

            EngineOptions.Add(new UCIOption(namePartFixed, typePart, uciLogger, _loggerFactory));
        }

        return true;
    }

    private async Task<bool> Send(string command, string? flag, CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return false;

        Log(_logger, GetMessage(Messages.Engine_UCICommand, command));

        ReadReceivedLines();

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log(_logger, GetMessage(Messages.Engine_UCICommandCancel, command));
            return false;
        }

        TaskCompletionSource<string?>? localTcs;

        lock (_waitLock)
        {
            _waitFlag = flag;
            _waitTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            localTcs = _waitTcs;
        }

        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();

            if (string.IsNullOrEmpty(flag))
            {
                return true;
            }

            Task completedTask = await Task.WhenAny(localTcs!.Task, Task.Delay(_engineSettings.Timeout, cancellationToken)).ConfigureAwait(false);

            if (completedTask == localTcs.Task)
            {
                string? result = await localTcs.Task.ConfigureAwait(false);

                return result != null;
            }

            lock (_waitLock)
            {
                if (ReferenceEquals(_waitTcs, localTcs))
                {
                    localTcs.TrySetResult(null);
                }
            }

            return false;
        }
        catch (OperationCanceledException ex)
        {
            lock (_waitLock)
            {
                if (ReferenceEquals(_waitTcs, localTcs))
                {
                    localTcs.TrySetResult(null);
                }
            }

            Log(_logger, GetMessage(Messages.Engine_UCICommandCancel, command), ex);

            return false;
        }
        catch (TimeoutException ex)
        {
            lock (_waitLock)
            {
                if (ReferenceEquals(_waitTcs, localTcs))
                {
                    localTcs.TrySetResult(null);
                }
            }

            Log(_logger, GetMessage(Messages.Engine_UCICommandTimeout, command), ex);

            return false;
        }
        catch (Exception ex)
        {
            lock (_waitLock)
            {
                if (ReferenceEquals(_waitTcs, localTcs))
                {
                    localTcs.TrySetResult(null);
                }
            }

            Log(_logger, GetMessage(Messages.Engine_UCICommandFail, command), ex);

            return false;
        }
        finally
        {
            lock (_waitLock)
            {
                if (ReferenceEquals(_waitTcs, localTcs))
                {
                    _waitFlag = null;
                    _waitTcs = null;
                }
            }

            _sendLock.Release();
        }
    }

    private Task QuitEngine()
    {
        if (_process is null || _process.HasExited) return Task.CompletedTask;

        return Send("quit", null);
    }

    private List<string> ReadReceivedLines()
    {
        List<string> list = new List<string>();

        while (_receiveQueue.TryDequeue(out string? l))
        {
            list.Add(l);
        }

        return list;
    }
}
