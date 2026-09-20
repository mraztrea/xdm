using System;
using System.Collections.Generic;

namespace XDM.GtkUI.Utils.Tray
{
    public sealed class SniMenuItem
    {
        public uint Id { get; }
        public string Label { get; }
        public TrayMenuItemAction Action { get; }

        public SniMenuItem(uint id, string label, TrayMenuItemAction action)
        {
            Id = id;
            Label = label;
            Action = action;
        }
    }

    /// <summary>
    /// The com.canonical.dbusmenu layout this process publishes. Ids and order are fixed for the
    /// session: hosts address the entries by id when they report a click.
    /// </summary>
    public sealed class SniMenuLayout
    {
        public const int RootId = 0;
        public const uint RestoreItemId = 1;
        public const uint ExitItemId = 2;

        public uint Revision { get; }
        public IReadOnlyList<SniMenuItem> Items { get; }

        internal SniMenuLayout(uint revision, IReadOnlyList<SniMenuItem> items)
        {
            Revision = revision;
            Items = items;
        }

        public TrayMenuItemAction? ActionFor(uint id)
        {
            foreach (var item in Items)
            {
                if (item.Id == id)
                {
                    return item.Action;
                }
            }
            return null;
        }
    }

    public static class TrayMenuLayout
    {
        private const string RestoreFallbackLabel = "Restore Window";
        private const string ExitFallbackLabel = "Exit";

        public static SniMenuLayout Build(uint revision, string restoreLabel, string exitLabel)
        {
            var items = new[]
            {
                new SniMenuItem(SniMenuLayout.RestoreItemId, Label(restoreLabel, RestoreFallbackLabel), TrayMenuItemAction.RestoreWindow),
                new SniMenuItem(SniMenuLayout.ExitItemId, Label(exitLabel, ExitFallbackLabel), TrayMenuItemAction.ExitApplication)
            };
            return new SniMenuLayout(revision, items);
        }

        /// <summary>
        /// TextResource returns an empty string for a key that no language file defines; an empty menu
        /// entry would be unusable, so fall back to the English text.
        /// </summary>
        private static string Label(string label, string fallback)
        {
            return string.IsNullOrWhiteSpace(label) ? fallback : label;
        }
    }
}
