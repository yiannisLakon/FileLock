namespace FileLock.Core;

/// <summary>
/// Base type for all FileLock errors. Every message is safe to show directly to a
/// non-technical user — no crypto jargon, no stack traces.
/// </summary>
public class FileLockException : Exception
{
    public FileLockException(string message) : base(message) { }
    public FileLockException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The file is not a FileLock file, or its header is unsupported/corrupt.</summary>
public sealed class BadFormatException : FileLockException
{
    public BadFormatException() : base("This file isn't a FileLock file.") { }
    public BadFormatException(string message) : base(message) { }
}

/// <summary>
/// The authentication tag did not verify on unlock. This means either the password was
/// wrong or the file was modified/corrupted after it was locked — the two are
/// cryptographically indistinguishable, and we report them together.
/// </summary>
public sealed class WrongPasswordException : FileLockException
{
    public WrongPasswordException() : base("Wrong password, or the file was changed.") { }
}

/// <summary>The input exceeds the v1 in-memory size limit.</summary>
public sealed class FileTooLargeException : FileLockException
{
    public FileTooLargeException(long size, long max)
        : base($"This file is too big for FileLock (limit is {max / (1024 * 1024)} MB).")
    {
        Size = size;
        Max = max;
    }

    public long Size { get; }
    public long Max { get; }
}
