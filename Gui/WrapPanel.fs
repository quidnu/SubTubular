﻿namespace SubTubular.Gui

open Fabulous
open Fabulous.Avalonia
open Avalonia.Layout

[<AutoOpen>]
module WrapPanelBuilders =
    type Fabulous.Avalonia.View with

        /// <summary>Creates a <see cref="WrapPanel" /> with <see cref="WrapPanel.Orientation" /> set to <see cref="Orientation.Vertical" />
        /// rendering child elements from left to right while they fit the width and starting a new line when there is no space left
        /// (including any margins and borders). See <seealso href="https://docs.avaloniaui.net/docs/reference/controls/detailed-reference/wrappanel" />.</summary>
        static member VWrapEmpty() =
            WidgetBuilder<'msg, IFabVirtualizingStackPanel>(
                WrapPanel.WidgetKey,
                WrapPanel.Orientation.WithValue(Orientation.Vertical)
            )

        /// <summary>Creates a <see cref="WrapPanel" /> with <see cref="WrapPanel.Orientation" /> set to <see cref="Orientation.Horizontal" />
        /// rendering child elements from top to bottom while they fit the height and starting a new column when there is no space left
        /// (including any margins and borders). See <seealso href="https://docs.avaloniaui.net/docs/reference/controls/detailed-reference/wrappanel" />.</summary>
        static member HWrapEmpty() =
            WidgetBuilder<'msg, IFabVirtualizingStackPanel>(
                WrapPanel.WidgetKey,
                WrapPanel.Orientation.WithValue(Orientation.Horizontal)
            )
