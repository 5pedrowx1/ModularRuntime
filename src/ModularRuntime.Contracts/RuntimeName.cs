namespace ModularRuntime.Contracts;

/// <summary>
/// Validation rules shared by every runtime identifier: plugin IDs, event topics,
/// service IDs, extension point IDs, contribution IDs, and operation names.
/// </summary>
/// <remarks>
/// A valid name has 1-128 characters: lowercase ASCII letters, digits, and the separators
/// <c>.</c>, <c>_</c>, and <c>-</c>. Separators cannot be leading, trailing, or adjacent.
/// Names are compared ordinally.
/// </remarks>
public static class RuntimeName
{
    public const int MaxLength = 128;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        var previousWasSeparator = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var isSeparator = character is '.' or '_' or '-';
            var isAlphaNumeric = character is >= 'a' and <= 'z' or >= '0' and <= '9';

            if (!isSeparator && !isAlphaNumeric)
            {
                return false;
            }

            if (isSeparator && (index == 0 || index == value.Length - 1 || previousWasSeparator))
            {
                return false;
            }

            previousWasSeparator = isSeparator;
        }

        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="value"/> is not valid.</summary>
    public static void Validate(string? value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid runtime name. Use 1-{MaxLength} lowercase ASCII letters, " +
                "digits, '.', '_' or '-', with no leading, trailing, or repeated separators.",
                parameterName);
        }
    }
}
