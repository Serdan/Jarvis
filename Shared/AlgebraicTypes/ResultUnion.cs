using ExhaustiveMatching;

namespace Shared.AlgebraicTypes;

[AutoClosed]
public partial record ResultUnion<T>
{
    partial record Ok(T Value);
    
    partial record Error(Exception Exception);
}
