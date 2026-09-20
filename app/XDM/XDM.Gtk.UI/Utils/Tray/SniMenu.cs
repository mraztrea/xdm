using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace XDM.GtkUI.Utils.Tray
{
    /// <summary>
    /// Serves the tray menu through com.canonical.dbusmenu version 3 at /MenuBar. Hosts read the layout
    /// once and report user clicks by calling Event with the item id, which is why the ids in
    /// <see cref="SniMenuLayout"/> are stable. See
    /// specs/001-background-tray-icon/contracts/status-notifier-item.md §3.
    ///
    /// MessageWriter is a ref struct that carries the write position, so every helper that writes takes
    /// it by ref; passing it by value silently produces a malformed reply, which makes the bus daemon
    /// drop the connection.
    /// </summary>
    internal sealed class SniMenuHandler : IMethodHandler
    {
        private const string MenuInterface = "com.canonical.dbusmenu";
        private const string MenuStatus = "normal";
        private const string TextDirection = "ltr";
        private const uint Version = 3;
        private const string ClickedEvent = "clicked";

        private static readonly string[] PropertyNames = { "Version", "Status", "TextDirection", "IconThemePath" };

        private static readonly ReadOnlyMemory<byte> IntrospectionXml = Encoding.UTF8.GetBytes(
            "<interface name=\"" + MenuInterface + "\">" +
            "<property name=\"Version\" type=\"u\" access=\"read\"/>" +
            "<property name=\"Status\" type=\"s\" access=\"read\"/>" +
            "<property name=\"TextDirection\" type=\"s\" access=\"read\"/>" +
            "<property name=\"IconThemePath\" type=\"as\" access=\"read\"/>" +
            "<method name=\"GetLayout\">" +
            "<arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"as\"/>" +
            "<arg direction=\"out\" type=\"u\"/><arg direction=\"out\" type=\"a(ia{sv}av)\"/></method>" +
            "<method name=\"GetGroupProperties\"><arg direction=\"in\" type=\"ai\"/><arg direction=\"in\" type=\"as\"/>" +
            "<arg direction=\"out\" type=\"a(ia{sv})\"/></method>" +
            "<method name=\"GetProperty\"><arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"s\"/>" +
            "<arg direction=\"out\" type=\"v\"/></method>" +
            "<method name=\"Event\"><arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"s\"/>" +
            "<arg direction=\"in\" type=\"v\"/><arg direction=\"in\" type=\"u\"/></method>" +
            "<method name=\"EventGroup\"><arg direction=\"in\" type=\"a(isvu)\"/><arg direction=\"out\" type=\"ai\"/></method>" +
            "<method name=\"AboutToShow\"><arg direction=\"in\" type=\"i\"/><arg direction=\"out\" type=\"b\"/></method>" +
            "<method name=\"AboutToShowGroup\"><arg direction=\"in\" type=\"ai\"/>" +
            "<arg direction=\"out\" type=\"ai\"/><arg direction=\"out\" type=\"ai\"/></method>" +
            "<signal name=\"LayoutUpdated\"><arg type=\"u\"/><arg type=\"i\"/></signal>" +
            "<signal name=\"ItemsPropertiesUpdated\"><arg type=\"a(ia{sv})\"/><arg type=\"a(ia{sv})\"/></signal>" +
            "</interface>");

        private readonly SniMenuLayout layout;
        private readonly Action<TrayMenuItemAction> onAction;

        public SniMenuHandler(SniMenuLayout layout, Action<TrayMenuItemAction> onAction)
        {
            this.layout = layout;
            this.onAction = onAction;
        }

        public string Path => SniTrayIcon.MenuPath;

        public bool RunMethodHandlerSynchronously(Message message) => true;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            // A failing call must never take the menu (or the connection) down with it.
            try
            {
                return HandleAsync(context);
            }
            catch (Exception ex)
            {
                TraceLog.Log.Debug(ex, "Tray menu method handler failed");
                if (!context.ReplySent && !context.NoReplyExpected)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
                }
                return default;
            }
        }

        private ValueTask HandleAsync(MethodContext context)
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(new[] { IntrospectionXml });
                return default;
            }

            var request = context.Request;
            switch (request.InterfaceAsString)
            {
                case SniTrayIcon.PropertiesInterface:
                    HandleProperties(context, request.MemberAsString);
                    return default;
                case SniTrayIcon.PeerInterface:
                    DBusReplies.ReplyPing(context, request.MemberAsString);
                    return default;
                case MenuInterface:
                    HandleMenu(context, request.MemberAsString);
                    return default;
                default:
                    DBusReplies.ReplyUnknownMethod(context);
                    return default;
            }
        }

        private void HandleProperties(MethodContext context, string member)
        {
            switch (member)
            {
                case "GetAll":
                    ReplyAllProperties(context);
                    return;
                case "Get":
                    ReplyProperty(context);
                    return;
                case "Set":
                    DBusReplies.ReplyPropertyReadOnly(context);
                    return;
                default:
                    DBusReplies.ReplyUnknownMethod(context);
                    return;
            }
        }

        private void ReplyAllProperties(MethodContext context)
        {
            var writer = context.CreateReplyWriter("a{sv}");
            try
            {
                var dictionary = writer.WriteDictionaryStart();
                foreach (var name in PropertyNames)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    WriteProperty(ref writer, name);
                }
                writer.WriteDictionaryEnd(dictionary);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void ReplyProperty(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var iface = reader.ReadString();
            var property = reader.ReadString();
            if (iface != MenuInterface)
            {
                DBusReplies.ReplyUnknownProperty(context, iface, property);
                return;
            }

            var writer = context.CreateReplyWriter("v");
            try
            {
                WriteProperty(ref writer, property);
                context.Reply(writer.CreateMessage());
            }
            catch (ArgumentException ex)
            {
                DBusReplies.ReplyUnknownProperty(context, iface, property, ex.Message);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void WriteProperty(ref MessageWriter writer, string name)
        {
            switch (name)
            {
                case "Version":
                    writer.WriteVariantUInt32(Version);
                    break;
                case "Status":
                    writer.WriteVariantString(MenuStatus);
                    break;
                case "TextDirection":
                    writer.WriteVariantString(TextDirection);
                    break;
                case "IconThemePath":
                    writer.WriteSignature("as");
                    var paths = writer.WriteArrayStart(DBusType.String);
                    writer.WriteArrayEnd(paths);
                    break;
                default:
                    throw new ArgumentException($"Unknown property '{name}'");
            }
        }

        private void HandleMenu(MethodContext context, string member)
        {
            switch (member)
            {
                case "GetLayout":
                    ReplyLayout(context);
                    return;
                case "GetGroupProperties":
                    ReplyGroupProperties(context);
                    return;
                case "GetProperty":
                    ReplyItemProperty(context);
                    return;
                case "Event":
                    HandleEvent(context);
                    return;
                case "EventGroup":
                    ReplyEmptyIdArray(context);
                    return;
                case "AboutToShow":
                    ReplyAboutToShow(context);
                    return;
                case "AboutToShowGroup":
                    ReplyAboutToShowGroup(context);
                    return;
                default:
                    DBusReplies.ReplyUnknownMethod(context);
                    return;
            }
        }

        private void ReplyLayout(MethodContext context)
        {
            var writer = context.CreateReplyWriter("ua(ia{sv}av)");
            try
            {
                writer.WriteUInt32(layout.Revision);
                var items = writer.WriteArrayStart(DBusType.Struct);
                writer.WriteStructureStart();
                writer.WriteInt32(SniMenuLayout.RootId);
                WriteItemProperties(ref writer, null);
                var children = writer.WriteArrayStart(DBusType.Variant);
                foreach (var item in layout.Items)
                {
                    writer.WriteSignature("(ia{sv}av)");
                    writer.WriteStructureStart();
                    writer.WriteInt32((int)item.Id);
                    WriteItemProperties(ref writer, item.Label);
                    var none = writer.WriteArrayStart(DBusType.Variant);
                    writer.WriteArrayEnd(none);
                }
                writer.WriteArrayEnd(children);
                writer.WriteArrayEnd(items);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void ReplyGroupProperties(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var end = reader.ReadArrayStart(DBusType.Int32);
            var ids = new List<int>();
            while (reader.HasNext(end))
            {
                ids.Add(reader.ReadInt32());
            }

            var writer = context.CreateReplyWriter("a(ia{sv})");
            try
            {
                var items = writer.WriteArrayStart(DBusType.Struct);
                foreach (var id in ids)
                {
                    writer.WriteStructureStart();
                    writer.WriteInt32(id);
                    WriteItemProperties(ref writer, LabelFor(id));
                }
                writer.WriteArrayEnd(items);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void ReplyItemProperty(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var id = reader.ReadInt32();
            var name = reader.ReadString();
            if (name != "label" && name != "enabled" && name != "visible")
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"Unknown property '{name}'");
                return;
            }

            var writer = context.CreateReplyWriter("v");
            try
            {
                if (name == "label")
                {
                    writer.WriteVariantString(LabelFor(id) ?? string.Empty);
                }
                else
                {
                    writer.WriteVariantBool(true);
                }
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void HandleEvent(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var id = reader.ReadInt32();
            var eventId = reader.ReadString();
            DBusReplies.ReplyVoid(context);

            if (eventId == ClickedEvent && layout.ActionFor((uint)id) is TrayMenuItemAction action)
            {
                onAction(action);
            }
        }

        private static void ReplyEmptyIdArray(MethodContext context)
        {
            using var writer = context.CreateReplyWriter("ai");
            var ids = writer.WriteArrayStart(DBusType.Int32);
            writer.WriteArrayEnd(ids);
            context.Reply(writer.CreateMessage());
        }

        private static void ReplyAboutToShow(MethodContext context)
        {
            using var writer = context.CreateReplyWriter("b");
            writer.WriteBool(false);
            context.Reply(writer.CreateMessage());
        }

        private static void ReplyAboutToShowGroup(MethodContext context)
        {
            using var writer = context.CreateReplyWriter("(aiai)");
            writer.WriteStructureStart();
            var updated = writer.WriteArrayStart(DBusType.Int32);
            writer.WriteArrayEnd(updated);
            var removed = writer.WriteArrayStart(DBusType.Int32);
            writer.WriteArrayEnd(removed);
            context.Reply(writer.CreateMessage());
        }

        private void WriteItemProperties(ref MessageWriter writer, string? label)
        {
            var properties = writer.WriteDictionaryStart();
            if (label != null)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("label");
                writer.WriteVariantString(label);
            }
            writer.WriteDictionaryEntryStart();
            writer.WriteString("enabled");
            writer.WriteVariantBool(true);
            writer.WriteDictionaryEntryStart();
            writer.WriteString("visible");
            writer.WriteVariantBool(true);
            writer.WriteDictionaryEnd(properties);
        }

        private string? LabelFor(int id)
        {
            foreach (var item in layout.Items)
            {
                if (item.Id == (uint)id)
                {
                    return item.Label;
                }
            }
            return null;
        }
    }
}
