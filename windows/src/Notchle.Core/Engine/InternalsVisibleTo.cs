using System.Runtime.CompilerServices;

// The judge, RNG and parsers expose their building blocks as `internal` for the tests, like the
// Swift suite does through @testable import.
[assembly: InternalsVisibleTo("Notchle.Core.Tests")]
