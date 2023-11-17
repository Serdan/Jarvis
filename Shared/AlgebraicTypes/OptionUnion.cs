using ExhaustiveMatching;

namespace Shared;

[AutoClosed]
public partial record OptionUnion<TValue>
{
    partial record Some(TValue Value);

    partial record None;
}
