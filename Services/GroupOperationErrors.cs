namespace Wayfarer.Services;

/// <summary>Publishes only the existing bounded group/invitation rules for the operation that exposed them.</summary>
public static class GroupOperationErrors
{
    /// <summary>Maps known service outcomes without exposing unrelated exceptions of the same CLR type.</summary>
    public static string Message(Exception exception, string operation)
    {
        var message = exception.Message;
        var allowed = operation switch
        {
            "create" => exception is InvalidOperationException &&
                message == "Group with the same name already exists for owner",
            "remove" => exception is InvalidOperationException && message is
                "Cannot remove owner from Organization group without an eligible Manager successor. Promote a member to Manager first or delete the group." or
                "Cannot remove the last manager from an Organization group.",
            "leave" => exception is InvalidOperationException && message is
                "You are the owner of this Organization group and there is no eligible Manager successor. Promote a member to Manager first or delete the group." or
                "You are the last manager of this Organization group. Transfer or add another manager before leaving.",
            "invite" => (exception is InvalidOperationException &&
                message == "A pending invitation already exists for this user in the specified group") ||
                (exception is ArgumentException && message == "Either inviteeUserId or inviteeEmail must be provided"),
            "accept" => exception is InvalidOperationException && message is
                "Invitation expired" or "Invitation is not pending",
            "decline" => exception is InvalidOperationException && message == "Invitation is not pending",
            _ => false
        };
        return allowed ? message : "The operation could not be completed. Please try again.";
    }
}
