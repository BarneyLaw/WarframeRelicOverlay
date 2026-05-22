namespace Infrastructure.Platform;

public interface IProcessTracker
{
    void TrackProcess(string processName);
}