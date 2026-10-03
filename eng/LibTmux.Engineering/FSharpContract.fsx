#r "FSharp.Compiler.Service.dll"

open System
open System.IO
open System.Text.RegularExpressions
open System.Text.Json
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

if arguments.Length <> 7 then
    invalidArg
        "arguments"
        "Expected the LibTmux, LibTmux.FSharp, LibTmux.Query.Json, LibTmux.Testing, LibTmux.Workspace, README and F# documentation paths."

let coreAssembly = Path.GetFullPath(arguments[0])
let facadeAssembly = Path.GetFullPath(arguments[1])
let queryJsonAssembly = Path.GetFullPath(arguments[2])
let testingAssembly = Path.GetFullPath(arguments[3])
let workspaceAssembly = Path.GetFullPath(arguments[4])
let readme = Path.GetFullPath(arguments[5])
let documentation = Path.GetFullPath(arguments[6])
let checker = FSharpChecker.Create()

let fsharpCoreAssembly =
    use assets =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(readme), "obj", "project.assets.json")))

    let fsharpCore =
        assets.RootElement.GetProperty("libraries").EnumerateObject()
        |> Seq.find (fun library -> library.Name.StartsWith("FSharp.Core/", StringComparison.Ordinal))

    let fsharpCorePath = fsharpCore.Value.GetProperty("path").GetString()

    let compilePath =
        assets.RootElement
            .GetProperty("targets")
            .GetProperty("net10.0")
            .GetProperty(fsharpCore.Name)
            .GetProperty("compile")
            .EnumerateObject()
        |> Seq.exactlyOne
        |> fun item -> item.Name

    assets.RootElement.GetProperty("packageFolders").EnumerateObject()
    |> Seq.map (fun folder -> Path.Combine(folder.Name, fsharpCorePath, compilePath))
    |> Seq.tryFind File.Exists
    |> Option.defaultWith (fun () -> failwith "The restored FSharp.Core compile assembly is missing.")

let prelude =
    $"""#r @"{coreAssembly}"
#r @"{facadeAssembly}"
#r @"{queryJsonAssembly}"
#r @"{testingAssembly}"
#r @"{workspaceAssembly}"
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
        Regex.Matches(
            File.ReadAllText(path),
            "```fsharp(?:[ \\t]+run)?\\r?\\n(?<source>.*?)```",
            RegexOptions.Singleline
        )

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
            Name = "unsupported-field"
            ShouldCompile = false
            Source = opens + "let invalid = PaneFields.mode"
        }
    ]

let check contract =
    let path =
        Path.Combine(Path.GetTempPath(), $"libtmux-fsharp-contract-{contract.Name}.fsx")

    let text = SourceText.ofString (prelude + contract.Source)

    let scriptOptions, optionDiagnostics =
        checker.GetProjectOptionsFromScript(path, text, assumeDotNetFramework = false)
        |> Async.RunSynchronously

    let isFsharpCoreReference (option: string) =
        option.StartsWith("-r:", StringComparison.Ordinal)
        && String.Equals(Path.GetFileName(option.Substring(3)), "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase)

    if
        scriptOptions.OtherOptions |> Array.filter isFsharpCoreReference |> Array.length
        <> 1
    then
        failwith "The F# script did not resolve exactly one FSharp.Core reference."

    let options =
        { scriptOptions with
            OtherOptions =
                scriptOptions.OtherOptions
                |> Array.map (fun option ->
                    if isFsharpCoreReference option then
                        "-r:" + fsharpCoreAssembly
                    else
                        option)
        }

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
