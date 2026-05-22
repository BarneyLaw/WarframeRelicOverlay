namespace Infrastructure.Platform;

public class WarframeProcessTracker : IProcessTracker
{

    private event Action<int>? WarframeStarted;
    private event Action<int>? WarframeStopped;

    
    public void TrackProcess(string processName)
    {

    }
}