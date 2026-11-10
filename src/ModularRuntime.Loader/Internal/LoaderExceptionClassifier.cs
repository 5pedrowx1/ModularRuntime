namespace ModularRuntime.Loader.Internal;

internal static class LoaderExceptionClassifier
{
    public static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException;
}
