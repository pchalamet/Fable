module Fable.Tests.SeqExpressionTests

open Util.Testing

type 'a Tree =
    | Leaf
    | Node of 'a Tree * 'a * 'a Tree

// type DisposableAction(f) =
//     interface System.IDisposable with
//         member _.Dispose() = f()

[<Fact>]
let ``empty seq expressions work`` () =
    let empty: int seq = seq { do () }
    empty |> Seq.length |> equal 0

[<Fact>]
let ``yield in seq expressions works`` () =
    seq { yield 1 }
    |> Seq.length |> equal 1

[<Fact>]
let ``yield from in seq expressions works`` () =
    let ys = seq { yield 1 }
    seq { yield! ys }
    |> Seq.length |> equal 1

// [<Fact>]
// let ``infinite seq expressions work`` () =
//     let rec green () = seq {
//         yield "green"
//         yield! yellow() }
//     and yellow () = seq {
//         yield "yellow"
//         yield! red() }
//     and red () = seq {
//         yield "red"
//         yield! green() }
//     green() |> Seq.item 12 |> equal "green"

[<Fact>]
let ``combine in seq expressions works`` () =
    seq { yield 1; yield 2 }
    |> Seq.length |> equal 2

[<Fact>]
let ``multiple yields in seq expressions work`` () =
    let upTo n = seq {
        for i = 1 to n do yield n
    }
    let numbers () = seq {
        yield 8
        yield! upTo 5
        yield 4
        yield! upTo 3
        yield 2
    }
    numbers() |> Seq.sum |> equal 48

[<Fact>]
let ``for in seq expressions works`` () =
    seq { for x in 1 .. 10 do yield x }
    |> Seq.length |> equal 10

[<Fact>]
let ``while in seq expressions works`` () =
    let mutable n = 0
    seq {
        while n < 10 do
            n <- n + 1
            yield n
    } |> Seq.sum |> equal 55

[<Fact>]
let ``recursive seq expressions work`` () =
    let rec traverse t = seq {
        match t with
        | Leaf -> ()
        | Node(xs, y, zs) ->
            yield y
            yield! traverse xs
            yield! traverse zs |> Seq.map (fun x -> 2 * x)
    }
    let t = Node(Node(Leaf, 1, Leaf), 2, Node(Leaf, 3, Leaf))
    traverse t |> Seq.sum |> equal 9

// [<Fact>]
// let ``try...finally in seq expressions works`` () =
//     let mutable n = 0
//     try seq {
//         try
//             raise (exn "My message")
//         finally
//             n <- n + 1
//         } |> Seq.iter ignore
//     with _ -> ()
//     equal 1 n

// [<Fact>]
// let ``use in seq expressions works`` () =
//     let mutable n = 0
//     seq {
//         use x = new DisposableAction(fun () ->
//             n <- n + 1)
//         ignore x
//     } |> Seq.iter ignore
//     equal 1 n

[<Fact>]
let ``array expressions work`` () =
    [| for x in 1 .. 10 do yield x |]
    |> Array.length |> equal 10

[<Fact>]
let ``list expressions work`` () =
    [ for x in 1 .. 10 do yield x ]
    |> List.length |> equal 10
