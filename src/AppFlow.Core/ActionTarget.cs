namespace AppFlow.Core;

public enum TargetKind { Application, Website, Script }

public static class ActionTarget
{
    public static TargetKind Kind(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? TargetKind.Website : IsScript(value) ? TargetKind.Script : TargetKind.Application;
    public static bool IsScript(string value) => Path.GetExtension(value).ToLowerInvariant() is ".ps1" or ".bat" or ".cmd";
    public static string Website(string value)
    {
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsWellFormedOriginalString())
            throw new InvalidDataException("Введите адрес сайта http:// или https:// без логина и пароля.");
        return uri.AbsoluteUri;
    }
}
