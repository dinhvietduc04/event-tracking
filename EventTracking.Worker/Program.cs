namespace EventTracking.Worker;

public static class WorkerProgram
{
    public static async Task Main(string[] args) => await WorkerApplication.Build(args).RunAsync();
}
