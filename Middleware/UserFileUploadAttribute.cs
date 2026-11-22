namespace Wayfarer.Middleware;

/// <summary>Marks only user file-upload actions for the administrator's multipart request policy.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UserFileUploadAttribute : Attribute
{
    /// <summary>Lets a Web form receive localized policy feedback before MVC can render validation.</summary>
    public bool LocalizeRequestRefusal { get; set; }
}
