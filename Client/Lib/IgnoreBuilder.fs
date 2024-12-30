namespace Client.Lib

type IgnoreBuilder() =
    member inline _.Delay([<InlineIfLambda>] f) = f ()
    member inline _.Yield _ = ()
    member inline _.Combine((), ()) = ()
    member inline _.Zero() = ()
