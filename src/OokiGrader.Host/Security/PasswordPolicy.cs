namespace OokiGrader.Host.Security;

public static class PasswordPolicy
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234",
        "password123!",
        "administrator",
        "qwertyuiop12",
        "123456789012",
        "ookigrader123",
        "ooki-grader",
    };

    public static IReadOnlyList<string> Validate(string? password)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < 12)
        {
            errors.Add("パスワードは12文字以上にしてください。");
        }

        if (!string.IsNullOrEmpty(password) && Blocked.Contains(password))
        {
            errors.Add("推測されやすいパスワードは使用できません。");
        }

        if (!string.IsNullOrEmpty(password)
            && (password.Contains('\r') || password.Contains('\n') || password.Contains('\0')))
        {
            errors.Add("パスワードに使用できない文字が含まれています。");
        }

        return errors;
    }
}
