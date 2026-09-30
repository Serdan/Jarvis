module Client.ActivityNavigation

let normalizeSelection count selectedIndex =
    if count <= 0 then
        None
    else
        selectedIndex
        |> Option.defaultValue (count - 1)
        |> max 0
        |> min (count - 1)
        |> Some

let move count delta selectedIndex =
    normalizeSelection count selectedIndex
    |> Option.map (fun index -> index + delta |> max 0 |> min (count - 1))

let scrollOffsetForSelection count visibleRows currentScrollOffset selectedIndex =
    if count <= 0 || visibleRows <= 0 then
        0
    else
        let maxScrollOffset = max 0 (count - visibleRows)
        let currentScrollOffset = currentScrollOffset |> max 0 |> min maxScrollOffset
        let endExclusive = count - currentScrollOffset
        let startIndex = max 0 (endExclusive - visibleRows)

        if selectedIndex < startIndex then
            let newEndExclusive = min count (selectedIndex + visibleRows)
            count - newEndExclusive
        elif selectedIndex >= endExclusive then
            count - (selectedIndex + 1)
        else
            currentScrollOffset
        |> max 0
        |> min maxScrollOffset
