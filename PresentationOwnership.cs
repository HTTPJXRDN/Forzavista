namespace ForzavistaFreeRoam;

// A deferred restore belongs to the car/process that received the mutation.
// Reused vehicle addresses alone do not establish ownership after a car swap.
internal sealed record PresentationOwnership(int ProcessId, ulong Module, ulong Vehicle, ulong Component, byte Original)
{
    internal bool Matches(int processId, ulong module, ulong vehicle, ulong component) =>
        ProcessId == processId && Module == module && Vehicle == vehicle && Component == component;
}
