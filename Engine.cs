using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Promote;

internal class Engine : IDisposable
{
    public string EngineName { get; private set; } = string.Empty;
    public string EngineAuthor { get; private set; } = string.Empty;
    public List<UCIOption> EngineOptions { get; private set; } = new List<UCIOption>();

    private readonly EngineSettings _engineSettings;
    private readonly ILogger<Engine>? _logger;

    private ProcessStartInfo? _processInfo;
    private Process? _process;

    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();

    private readonly object _waitLock = new object();
    private TaskCompletionSource<string?>? _waitTcs;
    private string? _waitFlag;

    public Engine(IOptions<Settings>? options = null, ILogger<Engine>? logger = null)
    {
        _engineSettings = options?.Value.Engine ?? new EngineSettings();

        _logger = logger;
    }

    public void Dispose()
    {
        StopProcess();
    }

    public async Task<bool> Start(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(_engineSettings.Path, nameof(_engineSettings.Path));

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
                    Log(Utils.GetMessage(Messages.UCIStarted, EngineName, EngineAuthor));

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

        Log(Utils.GetMessage(Messages.UCIStopped, EngineName, EngineAuthor));

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

        if (uciType is not null)
        {
            uciType.Value = value;
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

    private bool StartProcess()
    {
        _processInfo = new ProcessStartInfo()
        {
            FileName = _engineSettings.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
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
            Log(Utils.GetMessage(Messages.ProcessStartFailed), ex);

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
            Log(Utils.GetMessage(Messages.UnexpectedTryKill), ex);
        }
        finally
        {
            try
            {
                _process.Dispose();
            }
            catch (Exception ex)
            {
                Log(Utils.GetMessage(Messages.DisposeFail), ex);
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
            if (string.IsNullOrEmpty(_waitFlag) || string.Equals(line, _waitFlag, StringComparison.OrdinalIgnoreCase))
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
                EngineOptions.Add(new UCIOption(namePart, string.Empty));

                continue;
            }

            string namePartFixed = opt.Substring(12, index - 12).Trim();
            string typePart = opt.Substring(index + 6).Trim();

            EngineOptions.Add(new UCIOption(namePartFixed, typePart));
        }

        return true;
    }

    private async Task<bool> Send(string command, string? flag, CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return false;

        Log(Utils.GetMessage(Messages.UCICommand, command));

        ReadReceivedLines();

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

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            cts.CancelAfter(_engineSettings.Timeout);

            Task completedTask = await Task.WhenAny(localTcs!.Task, Task.Delay(_engineSettings.Timeout, cts.Token)).ConfigureAwait(false);

            if (completedTask == localTcs.Task)
            {
                string? result = await localTcs.Task.ConfigureAwait(false);

                return result is not null;
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

            Log(Utils.GetMessage(Messages.UCICommandCancel, command), ex);

            throw;
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

            Log(Utils.GetMessage(Messages.UCICommandTimeout, command), ex);

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

            Log(Utils.GetMessage(Messages.UCICommandFail, command), ex);

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

    private void Log(string message, Exception? ex = null, [CallerMemberName] string memberName = "", [CallerLineNumber] int lineNumber = 0)
    {
        Utils.Log(_logger, message, ex, memberName, lineNumber);
    }
}
