#r "FSharp.Compiler.Service.dll"

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Xml.Linq
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

let context = FSharpDisplayContext.Empty
let checker = FSharpChecker.Create()

let checkDiagnostics diagnostics =
    for diagnostic: FSharpDiagnostic in diagnostics do
        if diagnostic.Severity = FSharpDiagnosticSeverity.Error then
            failwith (diagnostic.ToString())

let attributes (values: seq<FSharpAttribute>) =
    values
    |> Seq.map (fun attribute ->
        {|
            ``type`` = attribute.AttributeType.FullName
            arguments =
                attribute.ConstructorArguments
                |> Seq.map (fun (_, value) -> string value)
                |> Seq.toArray
            signature = attribute.Format(context)
        |})
    |> Seq.toArray

let constraintText (constraint': FSharpGenericParameterConstraint) =
    if constraint'.IsReferenceTypeConstraint then
        "not struct"
    elif constraint'.IsNonNullableValueTypeConstraint then
        "struct"
    elif constraint'.IsNotSupportsNullConstraint then
        "not null"
    elif constraint'.IsSupportsNullConstraint then
        "null"
    elif constraint'.IsComparisonConstraint then
        "comparison"
    elif constraint'.IsEqualityConstraint then
        "equality"
    elif constraint'.IsUnmanagedConstraint then
        "unmanaged"
    elif constraint'.IsRequiresDefaultConstructorConstraint then
        "new: unit -> 'T"
    elif constraint'.IsAllowsRefStructConstraint then
        "allows ref struct"
    elif constraint'.IsCoercesToConstraint then
        ":> " + constraint'.CoercesToTarget.Format(context)
    elif constraint'.IsEnumConstraint then
        "enum<" + constraint'.EnumConstraintTarget.Format(context) + ">"
    else
        failwith "The public F# signature contains a constraint the inventory does not represent."

let genericParameters (parameters: seq<FSharpGenericParameter>) =
    parameters
    |> Seq.map (fun parameter ->
        {|
            name = parameter.Name
            constraints = parameter.Constraints |> Seq.map constraintText |> Seq.sort |> Seq.toArray
        |})
    |> Seq.toArray

let inspect (assemblyPath: string) =
    let members = ResizeArray<Dictionary<string, obj>>()
    let assemblyPath = Path.GetFullPath(assemblyPath)
    let package = Path.GetFileNameWithoutExtension(assemblyPath)
    let xml = XDocument.Load(Path.ChangeExtension(assemblyPath, ".xml"))

    let documentation =
        xml.Descendants(XName.Get "member")
        |> Seq.map (fun member' -> member'.Attribute(XName.Get "name").Value, member')
        |> dict

    let corePath = Path.Combine(Path.GetDirectoryName(assemblyPath), "LibTmux.dll")
    let source = SourceText.ofString (sprintf "#r %A\n#r %A\n()" corePath assemblyPath)
    let filename = Path.Combine(Path.GetDirectoryName(assemblyPath), "inventory.fsx")

    let options, diagnostics =
        checker.GetProjectOptionsFromScript(filename, source, assumeDotNetFramework = false)
        |> Async.RunSynchronously

    checkDiagnostics diagnostics

    let parsed, answer =
        checker.ParseAndCheckFileInProject(filename, 0, source, options)
        |> Async.RunSynchronously

    checkDiagnostics parsed.Diagnostics

    let result =
        match answer with
        | FSharpCheckFileAnswer.Succeeded result -> result
        | FSharpCheckFileAnswer.Aborted -> failwith "F# compiled-signature analysis was aborted."

    checkDiagnostics result.Diagnostics

    let assembly =
        result.ProjectContext.GetReferencedAssemblies()
        |> Seq.find (fun assembly -> assembly.FileName = Some assemblyPath)

    let add id name parent kind signature exposed extra =
        let comment =
            match documentation.TryGetValue(id) with
            | true, element when
                element.Element(XName.Get "summary") <> null
                && not (String.IsNullOrWhiteSpace(element.Element(XName.Get "summary").Value))
                ->
                element.ToString(SaveOptions.DisableFormatting)
            | _ -> failwithf "Missing F# XML summary: %s" id

        let entry = Dictionary<string, obj>()

        for key, value in
            [
                "id", box id
                "package", box package
                "name", box name
                "declaringType", box parent
                "visibility", box "public"
                "kind", box kind
                "signature", box signature
                "language", box "F#"
                "documentation", box comment
                "implicitDeclaration", box false
                "accessor", box false
                "interfaces", box Array.empty<string>
                "exposedTypes", box exposed
                "attributes", box Array.empty<obj>
                "parameters", box Array.empty<obj>
                "argumentGroups", box Array.empty<int>
            ] do
            entry.Add(key, value)

        for key, value in extra do
            entry[key] <- value

        members.Add entry

    let rec visit (entity: FSharpEntity) =
        if entity.IsNamespace then
            for nested in entity.NestedEntities do
                visit nested
        elif entity.Accessibility.IsPublic then
            let kind =
                if entity.IsFSharpModule then "module"
                elif entity.IsFSharpUnion then "union"
                elif entity.IsFSharpRecord then "record"
                elif entity.IsFSharpAbbreviation then "abbreviation"
                else "type"

            add
                entity.XmlDocSig
                entity.DisplayName
                (entity.DeclaringEntity
                 |> Option.filter (fun parent -> not parent.IsNamespace)
                 |> Option.map (fun parent -> parent.XmlDocSig)
                 |> Option.defaultValue "")
                kind
                entity.FullName
                [| entity.FullName |]
                [
                    "attributes", box (attributes entity.Attributes)
                    "genericParameters", box (genericParameters entity.GenericParameters)
                    "interfaces", box (entity.AllInterfaces |> Seq.map (fun t -> t.Format(context)) |> Seq.toArray)
                    "publicRepresentation", box entity.RepresentationAccessibility.IsPublic
                ]

            for member' in entity.MembersFunctionsAndValues do
                if
                    member'.Accessibility.IsPublic
                    && not member'.IsCompilerGenerated
                    && not member'.IsUnionCaseTester
                    && not member'.IsPropertyGetterMethod
                    && not member'.IsPropertySetterMethod
                then
                    let signature =
                        member'.GetValSignatureText(context, Range.range0)
                        |> Option.defaultWith (fun () -> failwithf "Missing F# signature: %s" member'.XmlDocSig)

                    add
                        member'.XmlDocSig
                        member'.DisplayName
                        entity.XmlDocSig
                        "member"
                        signature
                        [| member'.FullType.Format(context) |]
                        [
                            "compiledName", box member'.CompiledName
                            "genericParameters", box (genericParameters member'.GenericParameters)
                            "attributes", box (attributes member'.Attributes)
                            "returnType", box (member'.ReturnParameter.Type.Format(context))
                            "argumentGroups", box (member'.CurriedParameterGroups |> Seq.map _.Count |> Seq.toArray)
                            "parameters",
                            box (
                                member'.CurriedParameterGroups
                                |> Seq.collect id
                                |> Seq.map (fun parameter ->
                                    {|
                                        name = Option.defaultValue "" parameter.Name
                                        ``type`` = parameter.Type.Format(context)
                                        optional = parameter.IsOptionalArg
                                    |})
                                |> Seq.toArray
                            )
                        ]

            for index, case in entity.UnionCases |> Seq.indexed do
                if case.Accessibility.IsPublic then
                    let fields =
                        case.Fields
                        |> Seq.map (fun field -> field.Name + ": " + field.FieldType.Format(context))
                        |> Seq.toArray

                    add
                        case.XmlDocSig
                        case.Name
                        entity.XmlDocSig
                        "unionCase"
                        (case.Name
                         + (if fields.Length = 0 then
                                ""
                            else
                                " of " + String.concat " * " fields))
                        fields
                        [ "attributes", box (attributes case.Attributes); "caseIndex", box index ]

            for field in entity.FSharpFields do
                if field.Accessibility.IsPublic then
                    let signature = field.FieldType.Format(context)

                    add
                        field.XmlDocSig
                        field.Name
                        entity.XmlDocSig
                        "field"
                        signature
                        [| signature |]
                        [
                            "mutable", box field.IsMutable
                            "attributes", box (attributes field.FieldAttributes)
                        ]

            for nested in entity.NestedEntities do
                visit nested

    for entity in assembly.Contents.Entities do
        visit entity

    let actualIds =
        members
        |> Seq.filter (fun member' -> unbox<string> member'["package"] = package)
        |> Seq.map (fun member' -> unbox<string> member'["id"])
        |> Set.ofSeq

    if actualIds <> Set.ofSeq documentation.Keys then
        failwithf "%s XML identities differ from its compiled F# signature." package

    if members.Count = 0 then
        failwith "No compiled F# public declarations found."

    let hash path =
        File.ReadAllBytes(path) |> SHA256.HashData |> Convert.ToHexStringLower

    {|
        package = package
        framework = Path.GetFileName(Path.GetDirectoryName(assemblyPath))
        assemblySha256 = hash assemblyPath
        xmlSha256 = hash (Path.ChangeExtension(assemblyPath, ".xml"))
        members =
            members
            |> Seq.sortBy (fun member' -> unbox<string> member'["id"])
            |> Seq.toArray
    |}

{|
    compiler = typeof<FSharpChecker>.Assembly.GetName().Version.ToString()
    assemblies = fsi.CommandLineArgs |> Array.skip 1 |> Array.map inspect
|}
|> JsonSerializer.Serialize
|> Console.WriteLine
