import Foundation

/// SplitMix64 (Steele, Lea, Flood 2014). Tiny, fast and, unlike `SystemRandomNumberGenerator`,
/// reproducible from a seed on every platform, which the engine needs for deterministic shuffles.
struct SplitMix64: RandomNumberGenerator, Sendable, Hashable {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }

    /// Fisher-Yates with our own index draw, so the order does not depend on how the standard
    /// library's `shuffle(using:)` happens to consume random numbers in a given Swift version.
    mutating func shuffle<T>(_ array: inout [T]) {
        guard array.count > 1 else { return }
        for i in stride(from: array.count - 1, to: 0, by: -1) {
            let j = Int(next() % UInt64(i + 1))
            array.swapAt(i, j)
        }
    }

    mutating func shuffled<T>(_ array: [T]) -> [T] {
        var copy = array
        shuffle(&copy)
        return copy
    }
}
