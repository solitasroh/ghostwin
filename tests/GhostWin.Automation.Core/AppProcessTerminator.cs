using System.Diagnostics;

namespace GhostWin.Automation.Core;

public sealed class AppProcessTerminator
{
    private readonly Func<int, IStartedProcess> _resolveProcess;

    public AppProcessTerminator()
        : this(pid => new StartedProcessAdapter(Process.GetProcessById(pid)))
    {
    }

    public AppProcessTerminator(Func<int, IStartedProcess> resolveProcess)
    {
        _resolveProcess = resolveProcess ?? throw new ArgumentNullException(nameof(resolveProcess));
    }

    public void TerminateByPid(int pid, TimeSpan gracefulTimeout)
    {
        if (pid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pid), "PID must be positive.");
        }

        IStartedProcess process;
        try
        {
            process = _resolveProcess(pid);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Terminate(process, gracefulTimeout);
    }

    public void Terminate(IStartedProcess process, TimeSpan gracefulTimeout)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (process.HasExited)
        {
            return;
        }

        process.CloseMainWindow();
        if (!process.WaitForExit(gracefulTimeout))
        {
            process.Kill();
            process.WaitForExit(gracefulTimeout);
        }
    }
}

public interface IStartedProcess
{
    bool HasExited { get; }

    void CloseMainWindow();

    bool WaitForExit(TimeSpan timeout);

    void Kill();
}

public sealed class StartedProcessAdapter(Process process) : IStartedProcess
{
    public bool HasExited => process.HasExited;

    public void CloseMainWindow()
    {
        process.CloseMainWindow();
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        return process.WaitForExit((int)timeout.TotalMilliseconds);
    }

    public void Kill()
    {
        process.Kill();
    }
}
