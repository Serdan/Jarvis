module Client.Lib.String

open System

let startsWith value (s: string) =
    s.StartsWith(value, StringComparison.InvariantCulture)

let tryIndexOf' (value: string) (startIndex: int) (s: string) =
    let index = s.IndexOf(value, startIndex, StringComparison.Ordinal)
    if index >= 0 then Some index else None

let tryIndexOf (value: string) (s: string) = tryIndexOf' value 0 s
