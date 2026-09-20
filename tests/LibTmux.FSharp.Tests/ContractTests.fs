namespace LibTmux.FSharp.Tests

open LibTmux
open LibTmux.FSharp
open Xunit

module ContractTests =
    [<Fact>]
    let ``exactlyOne distinguishes cardinality and stops after two matches`` () =
        Assert.Equal<Result<int, CardinalityError>>(Error NoMatches, Selection.exactlyOne Seq.empty)
        Assert.Equal<Result<int, CardinalityError>>(Ok 42, Selection.exactlyOne [ 42 ])
        let mutable disposed = false

        let source =
            seq {
                try
                    yield 1
                    yield 2
                    failwith "A third element must never be requested."
                finally
                    disposed <- true
            }

        Assert.Equal<Result<int, CardinalityError>>(Error MultipleMatches, Selection.exactlyOne source)
        Assert.True disposed

    [<Fact>]
    let ``unread relations remain explicit without contacting tmux`` () =
        let server =
            LibTmux.Server.Open(ServerConnectionOptions(SocketName = "fsharp-no-io"))

        match Snapshot.relation server.Panes with
        | Uncaptured(relation, depth) ->
            Assert.Equal("panes", relation)
            Assert.Equal(SnapshotDepth.Server, depth)
        | Captured _ -> failwith "An unqueried endpoint cannot contain captured panes."
