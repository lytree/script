#r "nuget: Avalonia, 11.3.12"
#r "nuget: Avalonia.Desktop, 11.3.12"
#r "nuget: Avalonia.Fonts.Inter, 11.3.12"
#r "nuget: Avalonia.Diagnostics, 11.3.12"
#r "nuget: Avalonia.Themes.Fluent, 11.3.12"
#r "nuget: Avalonia.FuncUI, 1.5.2"

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Themes.Fluent
open Avalonia.FuncUI.Hosts
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout

type MainWindow() =
    inherit HostWindow()

    let view =
        Component(fun ctx ->
            let state = ctx.useState 0

            DockPanel.create
                [ DockPanel.children
                      [ Button.create
                            [ Button.dock Dock.Bottom
                              Button.onClick (fun _ -> state.Current - 1 |> state.Set)
                              Button.content "-"
                              Button.horizontalAlignment HorizontalAlignment.Stretch ]
                        Button.create
                            [ Button.dock Dock.Bottom
                              Button.onClick (fun _ -> state.Current + 1 |> state.Set)
                              Button.content "+"
                              Button.horizontalAlignment HorizontalAlignment.Stretch ]
                        TextBlock.create
                            [ TextBlock.dock Dock.Top
                              TextBlock.fontSize 48.0
                              TextBlock.verticalAlignment VerticalAlignment.Center
                              TextBlock.horizontalAlignment HorizontalAlignment.Center
                              TextBlock.text (string state.Current) ] ] ])

    do
        base.Title <- "Counter Example"
        base.Height <- 400.0
        base.Width <- 400.0
        base.Content <- view

type App() =


    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktopLifetime ->
            let mainWindow = MainWindow()
            desktopLifetime.MainWindow <- mainWindow
        | _ -> ()



AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().StartWithClassicDesktopLifetime([||])
