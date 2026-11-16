namespace Chirpdesk.Core.Logging;

/// <summary>
/// A deliberately tiny logging seam. Diagnostics must never cost a NuGet package, and the
/// log is user-shareable, so nothing sensitive is ever passed to it (see AGENTS.md invariant 4).
/// </summary>
public interface ILog
{
    void Info(string message);

    void Warn(string message);

#pragma warning disable CA1716 // "Error" is the name every .NET logger uses; the VB keyword collision is irrelevant to this codebase.
    void Error(string message, Exception? exception = null);
#pragma warning restore CA1716
}
