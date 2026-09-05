namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — RunArtefactStorageException (Sprint 3 / US-S3-04).
//
// Exists for exactly one reason: to let Program.cs's startup boundary tell "this server's
// run-artefact storage could not be configured" apart from every OTHER ArgumentException that could
// escape AddVouchfxMcpServer + Host.Build(). Before this type, that catch reported ANY
// ArgumentException — including an unrelated one from the DI/host stack — as "vouchfx-mcp could not
// configure its run-artefact storage", which mislabels a genuine wiring bug as a storage problem and
// sends the operator to look at their workspace directory (a peer review's NIT).

/// <summary>
/// Thrown when a component that owns run artefacts under <see cref="Workspace.OutputDir"/> —
/// <see cref="FileRunRegistry"/> and <see cref="WorkspaceRunLock"/> — cannot be configured against
/// the workspace it was given, because its output directory does not resolve inside that
/// workspace's root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derives from <see cref="ArgumentException"/> on purpose, not from a fresh root.</b> The
/// condition genuinely IS a bad argument (an <c>outputDirectory</c> that escapes its workspace), the
/// existing XML-doc contracts on both constructors say <see cref="ArgumentException"/>, and any
/// caller that catches the base type keeps working unchanged. What the subtype adds is a name that
/// <c>Program.cs</c> can catch NARROWLY, so an unrelated <see cref="ArgumentException"/> from
/// anywhere else in registration reaches the operator as the unhandled programming error it is —
/// with its stack trace intact — rather than as a misleading one-line storage diagnosis.
/// </para>
/// <para>
/// <b>It is deliberately NOT used for I/O faults.</b> A read-only directory, an exhausted volume, or
/// a denied ACL is a RUNTIME storage condition with its own catalogued code
/// (<c>VFX-E-1502 RunNotRecorded</c>, retryable) and its own per-call handling in
/// <see cref="RunSuiteOrchestrator"/>. This type is only ever thrown at CONSTRUCTION, for a
/// configuration that can never work, which is why it is startup-fatal rather than per-call.
/// </para>
/// </remarks>
public sealed class RunArtefactStorageException : ArgumentException
{
    /// <summary>Creates the exception with the framework's default message.</summary>
    public RunArtefactStorageException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RunArtefactStorageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and an inner exception.</summary>
    public RunArtefactStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> naming <paramref name="paramName"/>.</summary>
    public RunArtefactStorageException(string message, string paramName)
        : base(message, paramName)
    {
    }
}
