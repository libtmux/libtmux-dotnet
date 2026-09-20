#r "FSharp.Compiler.Service.dll"

open System
open System.IO
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

type Contract =
    {
        Name: string
        ShouldCompile: bool
        Source: string
    }

let arguments = fsi.CommandLineArgs |> Array.skip 1

if arguments.Length <> 4 then
    invalidArg "arguments" "Expected the LibTmux, LibTmux.FSharp, README and F# documentation paths."

let coreAssembly = Path.GetFullPath(arguments[0])
let facadeAssembly = Path.GetFullPath(arguments[1])
let readme = Path.GetFullPath(arguments[2])
let documentation = Path.GetFullPath(arguments[3])
let checker = FSharpChecker.Create()

let prelude =
    $"""#r @"{coreAssembly}"
#r @"{facadeAssembly}"
"""

let opens =
    """open LibTmux
open LibTmux.FSharp
"""

let documentationContracts =
    let sources =
        readme
        :: (Directory.EnumerateFiles(documentation, "*.md", SearchOption.AllDirectories)
            |> Seq.sort
            |> Seq.toList)

    let fences path =
        Regex.Matches(File.ReadAllText(path), "```fsharp\\r?\\n(?<source>.*?)```", RegexOptions.Singleline)

    if (fences readme).Count = 0 then
        invalidOp $"The F# README has no F# fences: {readme}."

    [
        for path in sources do
            let documentFences = fences path

            for index in 0 .. documentFences.Count - 1 do
                {
                    Name =
                        if path = readme && index = 0 then
                            "golden"
                        else
                            $"{Path.GetFileNameWithoutExtension(path)}-{index + 1}"
                    ShouldCompile = true
                    Source = documentFences[index].Groups["source"].Value.TrimEnd()
                }
    ]

let contracts =
    [
        yield! documentationContracts
        {
            Name = "wrong-operator"
            ShouldCompile = false
            Source = opens + "let invalid = Filter.startsWith \"yes\" SessionFields.attached"
        }
        {
            Name = "wrong-target"
            ShouldCompile = false
            Source = opens + "let invalid = Filter.eq (PaneId 1) SessionFields.id"
        }
        {
            Name = "wrong-relation"
            ShouldCompile = false
            Source =
                opens
                + """let invalid =
    Filter.eq "nvim" PaneFields.currentCommand
    |> Filter.any SessionFields.windows
"""
        }
        {
            Name = "descriptor-constructor"
            ShouldCompile = false
            Source =
                opens
                + "let invalid = Field<Pane, string>(Unchecked.defaultof<System.Reflection.PropertyInfo>)"
        }
        {
            Name = "unsupported-path"
            ShouldCompile = false
            Source = opens + "let invalid = PaneFields.currentPath"
        }
    ]

let check contract =
    let path =
        Path.Combine(Path.GetTempPath(), $"libtmux-fsharp-contract-{contract.Name}.fsx")

    let text = SourceText.ofString (prelude + contract.Source)

    let options, optionDiagnostics =
        checker.GetProjectOptionsFromScript(path, text, assumeDotNetFramework = false)
        |> Async.RunSynchronously

    let _, checkedFile =
        checker.ParseAndCheckFileInProject(path, 0, text, options)
        |> Async.RunSynchronously

    let aborted, checkDiagnostics =
        match checkedFile with
        | FSharpCheckFileAnswer.Aborted -> true, [||]
        | FSharpCheckFileAnswer.Succeeded result -> false, result.Diagnostics |> Seq.toArray

    aborted,
    Array.append (optionDiagnostics |> Seq.toArray) checkDiagnostics
    |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)

let failures =
    contracts
    |> List.choose (fun contract ->
        let aborted, diagnostics = check contract
        let compiled = not aborted && diagnostics.Length = 0

        let summary =
            if aborted then
                "The F# compiler aborted."
            else
                diagnostics |> Array.map string |> String.concat Environment.NewLine

        if compiled = contract.ShouldCompile then
            printfn "PASS %s" contract.Name
            None
        else
            Some $"{contract.Name}: expected compilation={contract.ShouldCompile}, diagnostics:\n{summary}")

if not failures.IsEmpty then
    failwith (String.concat Environment.NewLine failures)
