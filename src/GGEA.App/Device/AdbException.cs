namespace GGEA.App.Device;

/// <summary>
/// ADB 相关错误，消息会直接展示给用户。
/// </summary>
public sealed class AdbException : Exception
{
    public AdbException(string message) : base(message)
    {
    }

    public AdbException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
