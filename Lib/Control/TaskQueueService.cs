using Npgsql;

namespace Lib.Control;

/// <summary>
/// Long-running queue-based executor. Polls control.task_queue for pending tasks
/// and executes them across multiple threads.
///
/// Threading model:
///   - 4 threads poll for execution_mode = 'parallel'
///   - 1 thread polls for execution_mode = 'serial'
///   - Each thread has its own DB connection (EF Core / Npgsql not thread-safe)
///   - Task claim uses FOR UPDATE SKIP LOCKED to prevent races
///
/// Exit behavior:
///   - When ALL threads find empty queue, service exits
///   - No SIGINT handler — die immediately on kill, try/finally marks Failed on way out
/// </summary>
public class TaskQueueService
{
    private const int ParallelThreadCount = 4;
    private const int PollIntervalMs = 5000;
    private const int IdleCheckIntervalMs = 30000; // 30 seconds when all threads idle
    private const int MaxIdleCycles = 10; // 10 cycles = 5 minutes

    private volatile bool _shutdownRequested;
    private readonly int _totalThreadCount = ParallelThreadCount + 1; // +1 for serial
    private readonly bool[] _threadIdle; // per-thread idle flag
    private volatile int _allIdleCycleCount = 0; // Counter for consecutive idle cycles

    public TaskQueueService()
    {
        _threadIdle = new bool[_totalThreadCount];
    }