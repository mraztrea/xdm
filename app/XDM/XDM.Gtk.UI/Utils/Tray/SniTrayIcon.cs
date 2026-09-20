using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;
using TraceLog;

namespace XDM.GtkUI.Utils.Tray
{
    /// <summary>
    /// Publishes the tray icon through the freedesktop StatusNotifierItem protocol, so that hosts which
    /// do not understand the legacy XEmbed tray (GNOME Shell, and every Wayland session) can show it.
    /// The icon's menu is served by <see cref="SniMenuHandler"/> at /MenuBar.
    /// </summary>
    internal sealed class SniTrayIcon : ITrayBackend
    {
        internal const string ItemPath = "/StatusNotifierItem";
        internal const string MenuPath = "/MenuBar";
        internal const string ItemInterface = "org.kde.StatusNotifierItem";
        internal const string ItemInterfaceAlias = "org.freedesktop.StatusNotifierItem";
        internal const string WatcherInterface = "org.kde.StatusNotifierWatcher";
        internal const string WatcherPath = "/StatusNotifierWatcher";
        internal const string PropertiesInterface = "org.freedesktop.DBus.Properties";
        internal const string PeerInterface = "org.freedesktop.DBus.Peer";

        private static readonly string[] WatcherNames =
        {
            "org.kde.StatusNotifierWatcher",
            "org.freedesktop.StatusNotifierWatcher"
        };

        private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(5);

        private readonly SniMenuLayout layout;
        private readonly TrayIconPixmap[] pixmaps;
        private readonly Action<TrayMenuItemAction> onAction;

        private Connection? connection;
        private bool cancelled;

        public SniTrayIcon(SniMenuLayout layout, TrayIconPixmap[] pixmaps, Action<TrayMenuItemAction> onAction)
        {
            this.layout = layout;
            this.pixmaps = pixmaps;
            this.onAction = onAction;
        }

        public bool Attach()
        {
            if (pixmaps.Length == 0)
            {
                Log.Debug("StatusNotifierItem requires icon artwork, none is available");
                return false;
            }
            var attachTask = Task.Run(AttachAsync);
            if (!attachTask.Wait(AttachTimeout))
            {
                Log.Debug("StatusNotifierItem registration timed out");
                cancelled = true;
                Teardown();
                return false;
            }
            return attachTask.Result;
        }

        public void Dispose()
        {
            cancelled = true;
            Teardown();
        }

        private async Task<bool> AttachAsync()
        {
            try
            {
                var bus = new Connection(Address.Session);
                connection = bus;
                await bus.ConnectAsync().ConfigureAwait(false);
                if (cancelled)
                {
                    Teardown();
                    return false;
                }

                bus.AddMethodHandler(new SniItemHandler(layout, pixmaps, onAction));
                bus.AddMethodHandler(new SniMenuHandler(layout, onAction));
                WatchForDisconnect(bus);

                var watcher = await FindWatcherWithHostAsync(bus).ConfigureAwait(false);
                if (watcher == null)
                {
                    Log.Debug("No StatusNotifierWatcher with a registered host on the session bus");
                    Teardown();
                    return false;
                }
                if (cancelled)
                {
                    Teardown();
                    return false;
                }

                await bus.CallMethodAsync(CreateRegisterMessage(bus, watcher)).ConfigureAwait(false);
                Log.Debug($"StatusNotifierItem registered with {watcher}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "StatusNotifierItem registration failed");
                Teardown();
                return false;
            }
        }

        /// <summary>
        /// A dropped connection is the difference between "the tray icon vanished" and a diagnosable
        /// failure, so the reason is recorded (FR-010).
        /// </summary>
        private static void WatchForDisconnect(Connection bus)
        {
            var name = bus.UniqueName;
            _ = Task.Run(async () =>
            {
                try
                {
                    var reason = await bus.DisconnectedAsync().ConfigureAwait(false);
                    Log.Debug($"StatusNotifierItem connection {name} lost: {reason}");
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "StatusNotifierItem disconnect watch failed");
                }
            });
        }

        /// <summary>
        /// A watcher without a host would accept the registration and show nothing, so the host check
        /// decides whether the SNI backend can be used at all.
        /// </summary>
        private static async Task<string?> FindWatcherWithHostAsync(Connection bus)
        {
            foreach (var name in WatcherNames)
            {
                try
                {
                    if (await IsHostRegisteredAsync(bus, name).ConfigureAwait(false))
                    {
                        return name;
                    }
                    Log.Debug($"{name} has no registered host");
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, $"{name} is not available");
                }
            }
            return null;
        }

        private static async Task<bool> IsHostRegisteredAsync(Connection bus, string watcher)
        {
            return await bus.CallMethodAsync(
                CreateHostRegisteredMessage(bus, watcher),
                static (Message message, object? _) => message.GetBodyReader().ReadVariantValue().GetBool(),
                null).ConfigureAwait(false);
        }

        private static MessageBuffer CreateHostRegisteredMessage(Connection bus, string watcher)
        {
            using var writer = bus.GetMessageWriter();
            writer.WriteMethodCallHeader(watcher, WatcherPath, PropertiesInterface, "Get", "ss");
            writer.WriteString(WatcherInterface);
            writer.WriteString("IsStatusNotifierHostRegistered");
            return writer.CreateMessage();
        }

        private static MessageBuffer CreateRegisterMessage(Connection bus, string watcher)
        {
            using var writer = bus.GetMessageWriter();
            writer.WriteMethodCallHeader(watcher, WatcherPath, WatcherInterface, "RegisterStatusNotifierItem", "s");
            writer.WriteString(bus.UniqueName ?? string.Empty);
            return writer.CreateMessage();
        }

        private void Teardown()
        {
            var bus = connection;
            connection = null;
            if (bus == null)
            {
                return;
            }
            try
            {
                bus.RemoveMethodHandler(ItemPath);
                bus.RemoveMethodHandler(MenuPath);
                bus.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, ex.Message);
            }
        }
    }

    /// <summary>
    /// The StatusNotifierItem object: its properties (icon, tooltip, activation semantics) and the
    /// methods hosts call for user interaction. See
    /// specs/001-background-tray-icon/contracts/status-notifier-item.md.
    ///
    /// MessageWriter is a ref struct that carries the write position, so every helper that writes takes
    /// it by ref; passing it by value silently produces a malformed reply, which makes the bus daemon
    /// drop the connection.
    /// </summary>
    internal sealed class SniItemHandler : IMethodHandler
    {
        private const string Category = "ApplicationStatus";
        private const string Id = "xdm";
        private const string Title = "Xtreme Download Manager";
        private const string Status = "Active";
        private const string TooltipTitle = "XDM";
        private const string TooltipText = "Xtreme Download Manager";

        private static readonly string[] PropertyNames =
        {
            "Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap",
            "OverlayIconName", "OverlayIconPixmap", "AttentionIconName", "AttentionIconPixmap",
            "AttentionMovieName", "ToolTip", "ItemIsMenu", "Menu"
        };

        private static readonly ReadOnlyMemory<byte> IntrospectionXml = Encoding.UTF8.GetBytes(
            "<interface name=\"" + SniTrayIcon.ItemInterface + "\">" +
            "<property name=\"Category\" type=\"s\" access=\"read\"/>" +
            "<property name=\"Id\" type=\"s\" access=\"read\"/>" +
            "<property name=\"Title\" type=\"s\" access=\"read\"/>" +
            "<property name=\"Status\" type=\"s\" access=\"read\"/>" +
            "<property name=\"WindowId\" type=\"u\" access=\"read\"/>" +
            "<property name=\"IconName\" type=\"s\" access=\"read\"/>" +
            "<property name=\"IconPixmap\" type=\"a(iiay)\" access=\"read\"/>" +
            "<property name=\"OverlayIconName\" type=\"s\" access=\"read\"/>" +
            "<property name=\"OverlayIconPixmap\" type=\"a(iiay)\" access=\"read\"/>" +
            "<property name=\"AttentionIconName\" type=\"s\" access=\"read\"/>" +
            "<property name=\"AttentionIconPixmap\" type=\"a(iiay)\" access=\"read\"/>" +
            "<property name=\"AttentionMovieName\" type=\"s\" access=\"read\"/>" +
            "<property name=\"ToolTip\" type=\"(sa(iiay)ss)\" access=\"read\"/>" +
            "<property name=\"ItemIsMenu\" type=\"b\" access=\"read\"/>" +
            "<property name=\"Menu\" type=\"o\" access=\"read\"/>" +
            "<method name=\"ContextMenu\"><arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"i\"/></method>" +
            "<method name=\"Activate\"><arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"i\"/></method>" +
            "<method name=\"SecondaryActivate\"><arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"i\"/></method>" +
            "<method name=\"Scroll\"><arg direction=\"in\" type=\"i\"/><arg direction=\"in\" type=\"s\"/></method>" +
            "<signal name=\"NewIcon\"/><signal name=\"NewToolTip\"/><signal name=\"NewStatus\"><arg type=\"s\"/></signal>" +
            "<signal name=\"NewTitle\"/><signal name=\"NewAttentionIcon\"/>" +
            "</interface>");

        private readonly SniMenuLayout layout;
        private readonly TrayIconPixmap[] pixmaps;
        private readonly Action<TrayMenuItemAction> onAction;

        public SniItemHandler(SniMenuLayout layout, TrayIconPixmap[] pixmaps, Action<TrayMenuItemAction> onAction)
        {
            this.layout = layout;
            this.pixmaps = pixmaps;
            this.onAction = onAction;
        }

        public string Path => SniTrayIcon.ItemPath;

        public bool RunMethodHandlerSynchronously(Message message) => true;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            // A failing call must never take the icon (or the connection) down with it.
            try
            {
                return HandleAsync(context);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "StatusNotifierItem method handler failed");
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
                case SniTrayIcon.ItemInterface:
                case SniTrayIcon.ItemInterfaceAlias:
                    HandleItem(context, request.MemberAsString);
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
            if (iface != SniTrayIcon.ItemInterface && iface != SniTrayIcon.ItemInterfaceAlias)
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

        private void HandleItem(MethodContext context, string member)
        {
            switch (member)
            {
                // A primary (or middle) click is the user asking for the application window; the
                // coordinates are a hint we do not need.
                case "Activate":
                case "SecondaryActivate":
                    DBusReplies.ReplyVoid(context);
                    onAction(TrayMenuItemAction.RestoreWindow);
                    return;
                // The menu lives at /MenuBar and is opened by the host, so there is nothing to show here.
                case "ContextMenu":
                case "Scroll":
                    DBusReplies.ReplyVoid(context);
                    return;
                default:
                    DBusReplies.ReplyUnknownMethod(context);
                    return;
            }
        }

        private void WriteProperty(ref MessageWriter writer, string name)
        {
            switch (name)
            {
                case "Category":
                    writer.WriteVariantString(Category);
                    break;
                case "Id":
                    writer.WriteVariantString(Id);
                    break;
                case "Title":
                    writer.WriteVariantString(Title);
                    break;
                case "Status":
                    writer.WriteVariantString(Status);
                    break;
                case "WindowId":
                    writer.WriteVariantUInt32(0);
                    break;
                case "IconName":
                case "OverlayIconName":
                case "AttentionIconName":
                case "AttentionMovieName":
                    writer.WriteVariantString(string.Empty);
                    break;
                case "IconPixmap":
                    WritePixmaps(ref writer, pixmaps);
                    break;
                case "OverlayIconPixmap":
                case "AttentionIconPixmap":
                    WritePixmaps(ref writer, Array.Empty<TrayIconPixmap>());
                    break;
                case "ToolTip":
                    WriteToolTip(ref writer);
                    break;
                case "ItemIsMenu":
                    writer.WriteVariantBool(false);
                    break;
                case "Menu":
                    writer.WriteVariantObjectPath(SniTrayIcon.MenuPath);
                    break;
                default:
                    throw new ArgumentException($"Unknown property '{name}'");
            }
        }

        private static void WritePixmaps(ref MessageWriter writer, IReadOnlyList<TrayIconPixmap> icons)
        {
            writer.WriteSignature("a(iiay)");
            var array = writer.WriteArrayStart(DBusType.Struct);
            foreach (var icon in icons)
            {
                writer.WriteStructureStart();
                writer.WriteInt32(icon.Width);
                writer.WriteInt32(icon.Height);
                writer.WriteArray(icon.Argb32);
            }
            writer.WriteArrayEnd(array);
        }

        private static void WriteToolTip(ref MessageWriter writer)
        {
            writer.WriteSignature("(sa(iiay)ss)");
            writer.WriteStructureStart();
            writer.WriteString(string.Empty);
            var icons = writer.WriteArrayStart(DBusType.Struct);
            writer.WriteArrayEnd(icons);
            writer.WriteString(TooltipTitle);
            writer.WriteString(TooltipText);
        }
    }
}
