using System.Text.Json.Serialization;

namespace Shared;

/// <summary>
/// The contained string won't be serialized.
/// Use this type to signal your intent that the contained value should not be exposed externally.
/// </summary>
public readonly struct HiddenString([field: JsonIgnore] string value)
{
    public TResult Select<TResult>(Func<string, TResult> f) =>
        f(value);

    public override string ToString() =>
        nameof(HiddenString);
}
