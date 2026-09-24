namespace Notchle.Core;

/// SplitMix64 (Steele, Lea, Flood 2014), bit-for-bit identical to
/// Sources/NotchleCore/Engine/SplitMix64.swift so the same seed gives the same shuffle on both
/// platforms (shared vector: /spec/shuffle-vectors.json). A mutable struct: keep it in a field,
/// never copy it.
internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// Fisher-Yates with our own index draw, exactly as the Swift version consumes numbers.
    public T[] Shuffled<T>(IEnumerable<T> items)
    {
        var array = items.ToArray();
        for (var i = array.Length - 1; i > 0; i--)
        {
            var j = (int)(Next() % (ulong)(i + 1));
            (array[i], array[j]) = (array[j], array[i]);
        }
        return array;
    }
}
