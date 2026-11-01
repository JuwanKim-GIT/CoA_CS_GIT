using System;
using System.Windows;

namespace QCS
{
    internal static class QcsModal
    {
        // Owner dimming is restored even when a modal throws.
        public static bool? Show(Window dialog, Window? owner)
        {
            if (owner == null) return dialog.ShowDialog();
            double previous = owner.Opacity;
            try
            {
                dialog.Owner = owner;
                owner.Opacity = 0.72;
                return dialog.ShowDialog();
            }
            finally { owner.Opacity = previous; }
        }
    }
}
