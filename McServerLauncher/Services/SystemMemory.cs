namespace McServerLauncher.Services;

/// <summary>The machine's memory, as the memory budget needs it.</summary>
public static class SystemMemory
{
    /// <summary>The physical memory available to this process, in MB. 0 if it cannot be read.</summary>
    /// <remarks>
    /// The runtime's own figure: the installed memory, or a container's limit when there is one, on
    /// every system the app runs on, without any platform call.
    /// </remarks>
    public static long TotalMb()
    {
        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch
        {
            return 0;
        }
    }
}
