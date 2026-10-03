namespace LibTmux.FSharp

open LibTmux

// Each spec spells out its own ToString: the one F# would generate goes
// through FSharp.Core's reflection-based formatter, which NativeAOT rejects.

type SplitSpec =
    {
        Direction: PaneDirection option
        Command: string option
        Directory: string option
        Size: string option
        Environment: Map<string, string>
    }

    override this.ToString() =
        "split running " + defaultArg this.Command "the default shell"

type WindowSpec =
    {
        Name: string option
        Command: string option
        Directory: string option
        Environment: Map<string, string>
        Splits: SplitSpec list
    }

    override this.ToString() =
        "window " + defaultArg this.Name "named by tmux"

type SessionSpec =
    {
        Name: string
        Directory: string option
        Environment: Map<string, string>
        Windows: WindowSpec list
    }

    override this.ToString() = "session " + this.Name

[<RequireQualifiedAccess>]
module SplitSpec =
    let empty =
        {
            Direction = None
            Command = None
            Directory = None
            Size = None
            Environment = Map.empty
        }

[<RequireQualifiedAccess>]
module WindowSpec =
    let empty =
        {
            Name = None
            Command = None
            Directory = None
            Environment = Map.empty
            Splits = []
        }

    let named name = { empty with Name = Some name }

[<RequireQualifiedAccess>]
module SessionSpec =
    let named name =
        {
            Name = name
            Directory = None
            Environment = Map.empty
            Windows = []
        }
