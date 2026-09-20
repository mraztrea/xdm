using Tmds.DBus.Protocol;

namespace XDM.GtkUI.Utils.Tray
{
    /// <summary>Small shared helpers for answering D-Bus method calls.</summary>
    internal static class DBusReplies
    {
        public static void ReplyVoid(MethodContext context)
        {
            using var writer = context.CreateReplyWriter(string.Empty);
            context.Reply(writer.CreateMessage());
        }

        public static void ReplyPing(MethodContext context, string member)
        {
            if (member == "Ping")
            {
                ReplyVoid(context);
                return;
            }
            ReplyUnknownMethod(context);
        }

        public static void ReplyUnknownMethod(MethodContext context)
        {
            context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Unknown method");
        }

        public static void ReplyUnknownProperty(MethodContext context, string iface, string property, string? reason = null)
        {
            context.ReplyError(
                "org.freedesktop.DBus.Error.UnknownProperty",
                reason ?? $"Unknown property '{property}' on '{iface}'");
        }

        public static void ReplyPropertyReadOnly(MethodContext context)
        {
            context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "This property is read-only");
        }
    }
}
