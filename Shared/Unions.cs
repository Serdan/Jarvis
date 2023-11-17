using ExhaustiveMatching;

namespace Shared;

[AutoClosed]
public partial record OptionUnion<TValue>
{
    partial record Some(TValue Value);

    partial record None;
}

[AutoClosed]
public partial record ResultUnion<T>
{
    partial record Ok(T Value);
    
    partial record Error(AggregateException Exception);
}
