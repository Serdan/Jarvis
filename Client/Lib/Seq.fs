module Client.Lib.Seq

let filterAll filters source =
    source
    |> Seq.filter (fun item -> filters |> Seq.forall (fun filter -> filter item))
