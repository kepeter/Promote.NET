using System.Collections.Concurrent;
using System.Diagnostics;
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

    private readonly object _waitLock = new();
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
        ArgumentNullException.ThrowIfNullOrEmpty(_engineSettings.Path, nameof(_engineSettings.Path));

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
            }
        }

        return started;
    }

    public async Task Stop()
    {
        if (_process is null) return;

        await QuitEngine().ConfigureAwait(false);

        StopProcess();
    }

    public async Task NewGame()
    {
        await Send("ucinewgame", null).ConfigureAwait(false);

        await IsReady().ConfigureAwait(false);
    }

    public async Task<bool> IsReady()
    {
        return await Send("isready", "readyok");
    }

    public async Task<bool> SetDebug(bool on)
    {
        return await Send("debug " + (on ? "on" : "off"), null).ConfigureAwait(false);
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
            Log($"Failed to start process: {ex.GetType().Name}: {ex.Message}");

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
            Log($"TryKill unexpected error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                _process.Dispose();
            }
            catch (Exception ex)
            {
                Log($"Dispose failed: {ex.GetType().Name}: {ex.Message}");
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
            if (_waitTcs is not null)
            {
                if (string.IsNullOrEmpty(_waitFlag) || string.Equals(line, _waitFlag, StringComparison.OrdinalIgnoreCase))
                {
                    _waitTcs.TrySetResult(line);
                }
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

        ReadReceivedLines();

        try
        {
            lock (_waitLock)
            {
                _waitFlag = flag;
                _waitTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();

            if (string.IsNullOrEmpty(flag))
            {
                return true;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            cts.CancelAfter(_engineSettings.Timeout);

            Task completedTask = await Task.WhenAny(_waitTcs!.Task, Task.Delay(_engineSettings.Timeout, cts.Token)).ConfigureAwait(false);

            if (completedTask == _waitTcs.Task)
            {
                string? result = await _waitTcs.Task.ConfigureAwait(false);

                return result is not null;
            }

            lock (_waitLock)
            {
                _waitTcs.TrySetResult(null);
            }

            return false;
        }
        catch (OperationCanceledException ex)
        {
            lock (_waitLock)
            {
                _waitTcs?.TrySetResult(null);
            }

            Log($"Utils.GetMessage(Messages.UCICommandCancel, command): {ex.GetType().Name}: {ex.Message}");

            throw;
        }
        catch (TimeoutException ex)
        {
            lock (_waitLock)
            {
                _waitTcs?.TrySetResult(null);
            }

            Log($"Utils.GetMessage(Messages.UCICommandTimeout, command): {ex.GetType().Name}: {ex.Message}");

            return false;
        }
        catch (Exception ex)
        {
            lock (_waitLock)
            {
                _waitTcs?.TrySetResult(null);
            }

            Log($"Utils.GetMessage(Messages.UCICommandFail, command): {ex.GetType().Name}: {ex.Message}");

            return false;
        }
        finally
        {
            lock (_waitLock)
            {
                _waitFlag = null;
                _waitTcs = null;
            }
        }
    }

    private async Task QuitEngine()
    {
        if (_process is null || _process.HasExited) return;

        await Send("quit", null).ConfigureAwait(false);
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

    private void Log(string message)
    {
        if (_logger is not null)
        {
            _logger.LogInformation("{Message}", message);
        }
        else
        {
            Console.WriteLine(message);
        }
    }
}
