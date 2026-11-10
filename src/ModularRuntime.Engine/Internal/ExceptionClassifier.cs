namespace ModularRuntime.Engine.Internal;

/// <summary>
/// Fatal process exceptions are deliberately not converted into plugin failures.
/// </summary>
internal static class ExceptionClassifier
{
    public static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException;
}
