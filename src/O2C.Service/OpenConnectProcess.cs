using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using O2C.Core;
using O2C.Core.NetConfig;
using O2C.Core.OpenConnect;
using O2C.Service.Native;

namespace O2C.Service;

/// <summary>
/// Один запуск openconnect.exe: старт, чтение вывода, корректная остановка (Ctrl+C → openconnect
/// сам вызывает скрипт настройки сети на отключение и снимает маршруты), принудительное завершение как запасной путь.
/// </summary>
internal sealed class OpenConnectProcess : IDisposable
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>AttachConsole — глобальное состояние процесса, поэтому остановки идут строго по одной.</summary>
    private static readonly Lock ConsoleLock = new();

    private static readonly SafeFileHandle Job = CreateKillOnCloseJob();

    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Action<OpenConnectProcess, string>? _onOutput;

    public OpenConnectOutputParser Parser { get; } = new();

    public Task<int> Exited => _exited.Task;

    public int ProcessId => _process.Id;

    private OpenConnectProcess(Process process) => _process = process;

    static OpenConnectProcess()
    {
        // Ctrl+C, который мы шлём openconnect, приходит и в нашу консоль (при AttachConsole) — игнорируем его.
        NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, add: true);
    }

    public static OpenConnectProcess Start(ConnectionSettings settings, string password, Action<OpenConnectProcess, string> onOutput)
    {
        if (!File.Exists(ServicePaths.OpenConnectExe))
            throw new FileNotFoundException("Не найден openconnect.exe", ServicePaths.OpenConnectExe);

        var psi = new ProcessStartInfo(ServicePaths.OpenConnectExe)
        {
            WorkingDirectory = ServicePaths.OpenConnectDirectory,
            UseShellExecute = false,
            // Скрытая консоль нужна: через неё доставляется Ctrl+C для корректного отключения.
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Переменные окружения openconnect передаёт помощнику настройки сети.
        psi.Environment[TunnelPlan.DnsModeVariable] = settings.UseServerDns ? "server" : "local";
        foreach (var arg in OpenConnectCommand.BuildArguments(settings, ServicePaths.ConnectScript))
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var wrapper = new OpenConnectProcess(process);
        wrapper._onOutput = onOutput;

        process.OutputDataReceived += (_, e) => wrapper.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => wrapper.OnLine(e.Data);
        process.Exited += (_, _) =>
        {
            // WaitForExit() без таймаута дожидается дочитывания stdout/stderr.
            process.WaitForExit();
            wrapper._exited.TrySetResult(process.ExitCode);
        };

        process.Start();
        TryAssignToJob(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.StandardInput.WriteLine(password);
        process.StandardInput.Close();

        return wrapper;
    }

    /// <summary>Просит openconnect отключиться (Ctrl+C), по таймауту — завершает процесс.</summary>
    public async Task StopAsync()
    {
        if (_process.HasExited)
            return;

        SendCtrlC();

        var finished = await Task.WhenAny(Exited, Task.Delay(GracefulStopTimeout));
        if (finished != Exited && !_process.HasExited)
        {
            OnLine("Не удалось корректно отключиться за отведённое время, процесс завершён принудительно.");
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
            await Exited;
        }
    }

    private void SendCtrlC()
    {
        lock (ConsoleLock)
        {
            NativeMethods.FreeConsole();
            if (!NativeMethods.AttachConsole((uint)_process.Id))
            {
                OnLine($"AttachConsole не удался (код {Marshal.GetLastPInvokeError()}).");
                return;
            }
            try
            {
                if (!NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CTRL_C_EVENT, 0))
                    OnLine($"GenerateConsoleCtrlEvent не удался (код {Marshal.GetLastPInvokeError()}).");
            }
            finally
            {
                NativeMethods.FreeConsole();
            }
        }
    }

    private void OnLine(string? line)
    {
        if (line is null)
            return;
        Parser.ProcessLine(line);
        _onOutput?.Invoke(this, line);
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }

    private static void TryAssignToJob(Process process)
    {
        if (!Job.IsInvalid)
            NativeMethods.AssignProcessToJobObject(Job, process.Handle);
    }

    public void Dispose() => _process.Dispose();
}
